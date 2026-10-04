using System.Reflection;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MCPal.Server.Tests.Architecture;

/// <summary>
/// The onion rings: Domain, then Application, then the adapters (Storage, Infrastructure) and Web, then the host. A ring references only
/// rings inside it. The compiler lists only assemblies a project actually uses, so a forbidden using that compiles shows up here.
/// </summary>
[TestFixture]
internal sealed class ArchitectureTests
{
    private const string Domain = "MCPal.Server.Domain";
    private const string Application = "MCPal.Server.Application";
    private const string StorageRing = "MCPal.Server.Storage";
    private const string Infrastructure = "MCPal.Server.Infrastructure";
    private const string Web = "MCPal.Server.Web";

    /// <summary>Database providers, mail and the HTTP pipeline: details that only an adapter ring may know.</summary>
    private static readonly string[] ProviderAssemblies = ["Npgsql", "MailKit", "MimeKit"];

    private static readonly string[] EntityFrameworkAssemblies = ["Microsoft.EntityFrameworkCore"];

    private static readonly string[] HttpPipelineAssemblies =
    [
        "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.SignalR",
        "Microsoft.AspNetCore.Mvc",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Hosting",
        "ModelContextProtocol.AspNetCore",
    ];

    [Test]
    public void Domain_References_NoAdapterNoFrameworkDetail()
    {
        Forbidden(typeof(McpalOptions).Assembly, [Application, StorageRing, Infrastructure, Web, .. ProviderAssemblies, .. EntityFrameworkAssemblies, .. HttpPipelineAssemblies, "ModelContextProtocol"])
            .Should().BeEmpty();
    }

    [Test]
    public void Application_References_OnlyDomainAndProviderNeutralLibraries()
    {
        Forbidden(typeof(ApplicationModule).Assembly, [StorageRing, Infrastructure, Web, .. ProviderAssemblies, .. HttpPipelineAssemblies])
            .Should().BeEmpty();
    }

    [Test]
    public void Storage_References_OnlyDomain()
    {
        Forbidden(typeof(StorageModule).Assembly, [Application, Infrastructure, Web, "MailKit", "MimeKit", .. HttpPipelineAssemblies])
            .Should().BeEmpty();
    }

    [Test]
    public void Infrastructure_References_OnlyDomain()
    {
        Forbidden(typeof(InfrastructureModule).Assembly, [Application, StorageRing, Web, "Npgsql", .. EntityFrameworkAssemblies, .. HttpPipelineAssemblies])
            .Should().BeEmpty();
    }

    [Test]
    public void Web_References_ApplicationAndDomainButNoAdapter()
    {
        Forbidden(typeof(WebModule).Assembly, [StorageRing, Infrastructure, .. ProviderAssemblies])
            .Should().BeEmpty();
    }

    /// <summary>Hosted services start in registration order. The migration must finish before any background service queries the database.</summary>
    [Test]
    public async Task ServerModule_HostedServices_StartMigrationFirst()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(TestContext.CurrentContext.CancellationToken);

        var hosted = factory.Services.GetServices<IHostedService>()
            .Where(service => service.GetType().Namespace?.StartsWith("MCPal.Server", StringComparison.Ordinal) == true)
            .ToList();

        hosted.Should().HaveCountGreaterThan(1);
        hosted[0].Should().BeOfType<DatabaseMigrator>();
    }

    /// <summary>
    /// <c>AddIdentity</c> (web ring) and the Identity stores (storage ring) name the user and role types separately. They must agree,
    /// or the host fails only when it first resolves <c>UserManager</c>.
    /// </summary>
    [Test]
    public async Task ServerModule_WebHost_ResolvesIdentityManagersWithMatchingStores()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(TestContext.CurrentContext.CancellationToken);
        await using var scope = factory.Services.CreateAsyncScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<PortalUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        users.Should().NotBeNull();
        roles.Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IUserStore<PortalUser>>().Should().BeAssignableTo<IQueryableUserStore<PortalUser>>();
    }

    /// <summary>Guards the rules above against passing vacuously: the inward references they allow really are in the assembly's reference list.</summary>
    [Test]
    public void Rings_ReferenceTheRingInsideThem()
    {
        ReferencedNames(typeof(ApplicationModule).Assembly).Should().Contain(Domain);
        ReferencedNames(typeof(StorageModule).Assembly).Should().Contain(Domain);
        ReferencedNames(typeof(InfrastructureModule).Assembly).Should().Contain(Domain);
        ReferencedNames(typeof(WebModule).Assembly).Should().Contain([Application, Domain]);
    }

    private static string[] ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

    private static string[] Forbidden(Assembly assembly, string[] prefixes) =>
    [
        .. ReferencedNames(assembly)
            .Where(name => prefixes.Any(prefix => name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal),
    ];
}
