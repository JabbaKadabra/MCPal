using Autofac;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MCPal.Cloud.Tests.Infrastructure;

/// <summary>The real cloud host on an isolated database.</summary>
internal sealed class CloudWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string connectionString;
    private readonly Action<ContainerBuilder>? configureContainer;
    private readonly Dictionary<string, string?> settings;
    private readonly TimeProvider? timeProvider;

    private CloudWebApplicationFactory(string connectionString, Action<ContainerBuilder>? configureContainer, Dictionary<string, string?>? settings, TimeProvider? timeProvider)
    {
        this.timeProvider = timeProvider;
        this.connectionString = connectionString;
        this.configureContainer = configureContainer;
        this.settings = settings ?? [];
    }

    public static async Task<CloudWebApplicationFactory> CreateAsync(
        CancellationToken cancellationToken,
        Action<ContainerBuilder>? configureContainer = null,
        Dictionary<string, string?>? settings = null,
        TimeProvider? timeProvider = null,
        bool migrateOnStartup = false)
    {
        var connectionString = migrateOnStartup
            ? await PostgresFixture.CreateEmptyDatabaseAsync(cancellationToken)
            : await PostgresFixture.CreateDatabaseAsync(cancellationToken);
        settings = new Dictionary<string, string?>(settings ?? []) { ["Mcpal:MigrateOnStartup"] = migrateOnStartup ? "true" : "false" };
        return new CloudWebApplicationFactory(connectionString, configureContainer, settings, timeProvider);
    }

    /// <summary>Creates a company with one API key.</summary>
    public async Task<SeededCompany> SeedCompanyAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var company = await scope.ServiceProvider.GetRequiredService<ICompanyService>().CreateAsync(name, cancellationToken);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(company.Id, "test", null, cancellationToken);
        return new SeededCompany(company.Id, key.Id, key.RawKey);
    }

    public HubConnection CreateAgentConnection(string? apiKey)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hub/agent"), options =>
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

            configureContainer?.Invoke(container);
        });
        return base.CreateHost(builder);
    }
}

internal sealed record SeededCompany(Guid CompanyId, Guid ApiKeyId, string RawKey);
