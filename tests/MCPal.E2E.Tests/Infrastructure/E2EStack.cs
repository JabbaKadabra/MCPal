using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Bridge;
using MCPal.Bridge.Config;
using MCPal.Bridge.Tunnel;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace MCPal.E2E.Tests;

/// <summary>The MCPal server host on an isolated database, plus helpers to start in-process bridges and MCP clients against it.</summary>
internal sealed class E2EStack : WebApplicationFactory<MCPal.Server.ServerModule>
{
    private readonly string connectionString;
    private readonly Dictionary<string, string?> settings;

    private E2EStack(string connectionString, Dictionary<string, string?>? settings)
    {
        this.connectionString = connectionString;
        this.settings = settings ?? [];
    }

    /// <summary>
    /// The test MCP server runs from its own build output: it needs Microsoft.Extensions.Hosting, which this test project
    /// gets from the ASP.NET Core shared framework and therefore does not copy.
    /// </summary>
    private static string TestServerPath
    {
        get
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var framework = output.Name;
            var configuration = output.Parent?.Name ?? "Debug";
            var testsRoot = output.Parent?.Parent?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("Unexpected test output layout.");
            return Path.Combine(testsRoot, "MCPal.TestMcpServer", "bin", configuration, framework, "MCPal.TestMcpServer.dll");
        }
    }

    public static async Task<E2EStack> CreateAsync(CancellationToken cancellationToken, Dictionary<string, string?>? settings = null) =>
        new(await PostgresFixture.CreateDatabaseAsync(cancellationToken), settings);

    /// <summary>Creates a company with an owner, a bridge key (tunnel) and a personal access token of the owner (<c>/mcp</c>).</summary>
    public async Task<SeededCompany> SeedCompanyAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var company = await scope.ServiceProvider.GetRequiredService<ICompanyService>().CreateAsync(name, cancellationToken);
        var owner = await CreateUserAsync(scope.ServiceProvider, company.Id, $"owner@{company.Slug}.example", PortalRole.Owner);
        var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
        var bridgeKey = await keys.CreateAsync(company.Id, NewApiKey.Bridge("e2e-bridge", owner.Id), cancellationToken);
        var personalKey = await keys.CreateAsync(company.Id, NewApiKey.Personal("e2e-personal", owner.Id), cancellationToken);
        return new SeededCompany(company.Id, owner.Id, owner.Email ?? string.Empty, bridgeKey.Id, bridgeKey.RawKey, personalKey.Id, personalKey.RawKey);
    }

    /// <summary>Adds a confirmed user to a company and gives them a personal access token.</summary>
    public async Task<SeededUser> SeedUserAsync(Guid companyId, string email, PortalRole role, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var user = await CreateUserAsync(scope.ServiceProvider, companyId, email, role);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(companyId, NewApiKey.Personal("e2e-" + email, user.Id), cancellationToken);
        return new SeededUser(user.Id, email, key.Id, key.RawKey);
    }

    private static async Task<PortalUser> CreateUserAsync(IServiceProvider services, Guid companyId, string email, PortalRole role)
    {
        var user = new PortalUser { UserName = email, Email = email, EmailConfirmed = true, CompanyId = companyId, Role = role };
        var created = await services.GetRequiredService<UserManager<PortalUser>>().CreateAsync(user, "correct-horse-battery");
        created.Succeeded.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>Revokes a key of the company.</summary>
    public async Task RevokeAsync(SeededCompany company, Guid apiKeyId, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(company.CompanyId, apiKeyId, cancellationToken);
    }

    /// <summary>Starts a real bridge (config, local server manager, tunnel client) that talks to this server.</summary>
    public async Task<IHost> StartBridgeAsync(SeededCompany company, string serverName, string bridgeName, CancellationToken cancellationToken, TimeProvider? timeProvider = null, string? statusFile = null, string? protocolVersion = null, ILoggerProvider? logs = null)
    {
        var config = new BridgeConfig(
            new McpalConfig(Server.BaseAddress.ToString().TrimEnd('/'), company.BridgeKey, bridgeName),
            new Dictionary<string, LocalServerConfig>
            {
                [serverName] = new(
                    "dotnet",
                    [TestServerPath],
                    new Dictionary<string, string>(),
                    null,
                    new Dictionary<string, string>()),
            },
            BridgeConfig.DefaultCallTimeoutSeconds)
        {
            StatusFile = statusFile,
        };
        var builder = Host.CreateApplicationBuilder();
        if (logs is not null)
        {
            // The bridge's appsettings.json sets Information as default; a rule for the bridge's own categories is more specific.
            builder.Logging.AddFilter("MCPal.Bridge", LogLevel.Debug);
            builder.Logging.AddProvider(logs);
        }

        builder.ConfigureContainer(new AutofacServiceProviderFactory(), container =>
        {
            container.RegisterModule(new BridgeModule());
            if (protocolVersion is not null)
            {
                container.RegisterInstance(BridgeInfo.Current with { ProtocolVersion = protocolVersion }).AsSelf();
            }

            container.RegisterInstance(config).AsSelf();
            container.RegisterInstance(new InMemoryTransport(this)).As<ITunnelTransportConfigurator>();
            if (timeProvider is not null)
            {
                container.RegisterInstance(timeProvider).As<TimeProvider>();
            }
        });
        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    /// <summary>A client connected straight to the test MCP server, to compare what Claude gets through the MCPal server with the original.</summary>
    public static async Task<McpClient> ConnectLocalServerAsync(CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "local", Command = "dotnet", Arguments = [TestServerPath] });
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>A bare tunnel connection with the given key, e.g. to hold a server name like a stale bridge connection would.</summary>
    public HubConnection CreateTunnelConnection(string apiKey)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hub/bridge"), options =>
            {
                new InMemoryTransport(this).Configure(options);
                options.Headers["Authorization"] = "Bearer " + apiKey;
            })
            .Build();
    }

    public async Task<McpClient> ConnectClientAsync(string apiKey, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey },
            },
            CreateClient(),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>Polls until the client lists the tool, i.e. the bridge has registered.</summary>
    public static async Task WaitForToolAsync(McpClient client, string toolName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            if (tools.Any(t => t.Name == toolName))
            {
                return;
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mcpal"] = connectionString,
                ["Mcpal:MigrateOnStartup"] = "false",
            };
            foreach (var pair in settings)
            {
                values[pair.Key] = pair.Value;
            }

            configuration.AddInMemoryCollection(values);
        });
    }

    private sealed class InMemoryTransport(E2EStack stack) : ITunnelTransportConfigurator
    {
        public void Configure(HttpConnectionOptions options)
        {
            options.HttpMessageHandlerFactory = _ => stack.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
        }
    }
}

/// <param name="BridgeKey">Raw bridge key: opens the tunnel.</param>
/// <param name="PersonalKey">Raw personal access token of the owner: calls <c>/mcp</c>.</param>
internal sealed record SeededCompany(Guid CompanyId, string OwnerUserId, string OwnerEmail, Guid BridgeKeyId, string BridgeKey, Guid PersonalKeyId, string PersonalKey);

internal sealed record SeededUser(string UserId, string Email, Guid PersonalKeyId, string PersonalKey);
