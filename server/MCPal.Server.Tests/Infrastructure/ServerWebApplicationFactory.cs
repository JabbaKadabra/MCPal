using Autofac;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MCPal.Server.Tests.Infrastructure;

/// <summary>The real server host on an isolated database.</summary>
internal sealed class ServerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string connectionString;
    private readonly Action<ContainerBuilder>? configureContainer;
    private readonly Dictionary<string, string?> settings;
    private readonly TimeProvider? timeProvider;
    private readonly string? webRoot;

    private ServerWebApplicationFactory(string connectionString, Action<ContainerBuilder>? configureContainer, Dictionary<string, string?>? settings, TimeProvider? timeProvider, string? webRoot)
    {
        this.webRoot = webRoot;
        this.timeProvider = timeProvider;
        this.connectionString = connectionString;
        this.configureContainer = configureContainer;
        this.settings = settings ?? [];
    }

    /// <summary>Every mail the portal tries to send lands here instead of an SMTP server.</summary>
    public CapturingEmailSender Emails { get; } = new();

    public static async Task<ServerWebApplicationFactory> CreateAsync(
        CancellationToken cancellationToken,
        Action<ContainerBuilder>? configureContainer = null,
        Dictionary<string, string?>? settings = null,
        TimeProvider? timeProvider = null,
        bool migrateOnStartup = false,
        string? webRoot = null)
    {
        var connectionString = migrateOnStartup
            ? await PostgresFixture.CreateEmptyDatabaseAsync(cancellationToken)
            : await PostgresFixture.CreateDatabaseAsync(cancellationToken);
        settings = new Dictionary<string, string?>(settings ?? []) { ["Mcpal:MigrateOnStartup"] = migrateOnStartup ? "true" : "false" };
        return new ServerWebApplicationFactory(connectionString, configureContainer, settings, timeProvider, webRoot);
    }

    /// <summary>Creates a company with an owner, a bridge key (tunnel) and a personal access token of the owner (<c>/mcp</c>).</summary>
    public async Task<SeededCompany> SeedCompanyAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var company = await scope.ServiceProvider.GetRequiredService<ICompanyService>().CreateAsync(name, cancellationToken);
        var owner = await SeedUserAsync(scope.ServiceProvider, company.Id, $"owner@{company.Slug}.example", PortalRole.Owner);
        var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
        var bridgeKey = await keys.CreateAsync(company.Id, NewApiKey.Bridge("test-bridge", owner.Id), cancellationToken);
        var personalKey = await keys.CreateAsync(company.Id, NewApiKey.Personal("test-personal", owner.Id), cancellationToken);
        return new SeededCompany(company.Id, owner.Id, owner.Email ?? string.Empty, bridgeKey.Id, bridgeKey.RawKey, personalKey.Id, personalKey.RawKey);
    }

    /// <summary>Adds a confirmed user to a company and gives them a personal access token.</summary>
    public async Task<SeededUser> SeedUserAsync(Guid companyId, string email, PortalRole role, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var user = await SeedUserAsync(scope.ServiceProvider, companyId, email, role);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(companyId, NewApiKey.Personal("test-" + email, user.Id), cancellationToken);
        return new SeededUser(user.Id, email, key.Id, key.RawKey);
    }

    private static async Task<PortalUser> SeedUserAsync(IServiceProvider services, Guid companyId, string email, PortalRole role)
    {
        var user = new PortalUser { UserName = email, Email = email, EmailConfirmed = true, CompanyId = companyId, Role = role };
        var created = await services.GetRequiredService<UserManager<PortalUser>>().CreateAsync(user, PortalClient.Password);
        created.Succeeded.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>Stores an OAuth access token for the user as the OAuth server would after a completed flow, and returns the raw token.</summary>
    public async Task<string> IssueAccessTokenAsync(Guid companyId, string userId, string clientId, CancellationToken cancellationToken)
    {
        var token = "oauth-test-token-" + Guid.NewGuid().ToString("N");
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Hash = ApiKeyService.Hash(token),
            Kind = OAuthTokenKind.Access,
            CompanyId = companyId,
            UserId = userId,
            ClientId = clientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public HubConnection CreateBridgeConnection(string? apiKey)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hub/bridge"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (apiKey is not null)
                {
                    options.Headers["Authorization"] = "Bearer " + apiKey;
                }
            })
            .Build();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (webRoot is not null)
        {
            builder.ConfigureServices((context, _) =>
            {
                if (context.HostingEnvironment is IWebHostEnvironment environment)
                {
                    environment.WebRootPath = webRoot;
                    environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);
                }
            });
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?> { ["ConnectionStrings:Mcpal"] = connectionString };
            foreach (var pair in settings)
            {
                values[pair.Key] = pair.Value;
            }

            configuration.AddInMemoryCollection(values);
        });
    }

    /// <summary>
    /// Runs after the host's own container callbacks (and after the module's populated services), so overrides win.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureContainer<ContainerBuilder>(container =>
        {
            if (timeProvider is not null)
            {
                container.RegisterInstance(timeProvider).As<TimeProvider>();
            }

            container.RegisterInstance<IEmailSender>(Emails);

            configureContainer?.Invoke(container);
        });
        return base.CreateHost(builder);
    }
}

/// <param name="BridgeKey">Raw bridge key: opens the tunnel.</param>
/// <param name="PersonalKey">Raw personal access token of the owner: calls <c>/mcp</c>.</param>
internal sealed record SeededCompany(Guid CompanyId, string OwnerUserId, string OwnerEmail, Guid BridgeKeyId, string BridgeKey, Guid PersonalKeyId, string PersonalKey);

internal sealed record SeededUser(string UserId, string Email, Guid PersonalKeyId, string PersonalKey);
