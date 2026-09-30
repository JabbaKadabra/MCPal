using Autofac;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Infrastructure;

/// <summary>
/// Base harness. <see cref="GetServicesAsync"/> builds a fresh container and an isolated database per call from the production module.
/// </summary>
internal abstract class ServerTestBase
{
    private readonly List<IContainer> containers = [];

    protected static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    protected async Task<ILifetimeScope> GetServicesAsync(Action<ContainerBuilder>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var connectionString = await PostgresFixture.CreateDatabaseAsync(Ct);
        var values = new Dictionary<string, string?> { ["ConnectionStrings:Mcpal"] = connectionString };
        if (settings is not null)
        {
            foreach (var pair in settings)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = new ContainerBuilder();
        builder.RegisterInstance<IConfiguration>(configuration);
        builder.RegisterInstance(new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)))
            .As<TimeProvider>().As<FakeTimeProvider>();
        builder.RegisterModule(new ServerModule(registerWebServices: false));
        configure?.Invoke(builder);
        var container = builder.Build();
        containers.Add(container);
        return container.BeginLifetimeScope();
    }

    /// <summary>Inserts a confirmed user straight into the database (the harness has no Identity services).</summary>
    protected static async Task<PortalUser> CreateUserAsync(ILifetimeScope scope, Guid companyId, string email = "anna@acme.example", PortalRole role = PortalRole.Member)
    {
        var user = new PortalUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            CompanyId = companyId,
            Role = role,
        };
        var db = scope.Resolve<MCPalDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    [TearDown]
    public void DisposeContainers()
    {
        foreach (var container in containers)
        {
            container.Dispose();
        }

        containers.Clear();
    }
}
