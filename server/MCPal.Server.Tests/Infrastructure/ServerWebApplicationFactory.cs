using Autofac;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Hosting;
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

    /// <summary>Creates a company with one API key.</summary>
    public async Task<SeededCompany> SeedCompanyAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var company = await scope.ServiceProvider.GetRequiredService<ICompanyService>().CreateAsync(name, cancellationToken);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(company.Id, "test", null, cancellationToken);
        return new SeededCompany(company.Id, key.Id, key.RawKey);
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

internal sealed record SeededCompany(Guid CompanyId, Guid ApiKeyId, string RawKey);
