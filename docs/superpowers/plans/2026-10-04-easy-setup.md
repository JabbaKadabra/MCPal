# Bridge Enrollment Codes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An owner copies one command from the portal Setup page; the bridge redeems a single-use enrollment code for its own bridge key, so no key or URL is copied by hand.

**Architecture:** The server gets a `BridgeEnrollment` table, a `BridgeEnrollmentService` and two endpoints (owner creates a code, anonymous bridge redeems it). The bridge gets a credentials file, an `enroll` verb and automatic enrollment on `run` when `MCPAL_ENROLL` is set and no key exists. Installers, Docker files, the Setup page and the docs follow.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core + PostgreSQL, Autofac, NUnit 5 + NSubstitute + AwesomeAssertions, React + TypeScript + Vite + TanStack Query + vitest, bash and PowerShell installers.

**Spec:** `docs/superpowers/specs/2026-10-04-easy-setup-design.md`

## Global Constraints

- `dotnet build MCPal.slnx` must stay at zero warnings (warnings are errors).
- Types are `internal`; only interfaces, Contracts DTOs and modules are public. No `!`, `null!`, `default!`, no `#pragma warning disable` for nullability.
- Time only from injected `TimeProvider`; every async method takes and forwards a `CancellationToken`.
- No `lock` / raw `SemaphoreSlim` in feature code.
- Register every new server service in the module of its ring (`ApplicationModule` for `BridgeEnrollmentService`), or a minimal-API parameter is bound as request body and the host fails on startup.
- No global query filters: every query filters by company explicitly.
- Package versions only in `Directory.Packages.props`; this plan adds no package.
- Tests: names `Subject_Condition_ExpectedOutcome`, no instance fields or `[SetUp]` state, resolve the SUT from the container. Server tests extend `ServerTestBase` or use `ServerWebApplicationFactory`.
- Enrollment code: prefix `mcpale_` + 24 random characters of the key alphabet; stored only as SHA-256 hex; lifetime `Mcpal:EnrollmentLifetimeMinutes` (default 15); at most 10 open (unredeemed, unexpired) codes per company; every redemption failure answers `400 {"error":"invalid_code"}`.
- Key precedence in the bridge: `MCPAL_API_KEY`, then `mcpal.apiKey`, then credentials file. `MCPAL_ENROLL` is ignored when a key exists.
- Strings of the SPA go through the typed `t()` helper in `server/portal/src/i18n/en.ts`.
- Docs, comments and commit messages are normal English prose (no caveman style).
- Commit trailer for every commit (use this form, zsh):
  `git commit -m "type: subject" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'`
- Run commands from `/home/jabba/MCPal` (use absolute paths; the shell's directory can drift).

## Review Focus

Failure modes the spec implies but a straight reading of the tasks could miss. Each has a test in the named task.

1. Two bridges redeem the same code at the same instant: exactly one gets a key (Task 2).
2. A code made by company A must never produce a key for company B (Task 2 and Task 3).
3. A credentials file that already exists with wider permissions is narrowed to mode 600 on write (Task 4).
4. A container restarts with `MCPAL_ENROLL` still set after it enrolled: it must not call the server again and must not fail (Task 5).
5. Anonymous endpoint with an empty body or invalid JSON answers 400, not 500 and not the SPA's `index.html` (Task 3).
6. Bridge name that is empty, very long or holds control characters yields a sane key name (Task 2).
7. An expired code on the Setup page must not stay in the copy-paste command (Task 8).

## File Structure

Server:
- Create `server/MCPal.Server.Application/Tenancy/BridgeEnrollmentService.cs`: create and redeem codes.
- Create `server/MCPal.Server.Web/Tenancy/BridgeEnrollEndpoints.cs`: anonymous `POST /api/bridge/enroll`.
- Modify `server/MCPal.Server.Domain/Tenancy/Entities.cs` (entity), `server/MCPal.Server.Domain/McpalOptions.cs` (option), `server/MCPal.Server.Storage/MCPalDbContext.cs` (mapping), migration in `server/MCPal.Server.Storage/Migrations/`.
- Modify `server/MCPal.Server.Application/ApplicationModule.cs`, `server/MCPal.Server.Application/Tenancy/ApiKeyService.cs` (alphabet visibility), `server/MCPal.Server.Web/Portal/SetupEndpoints.cs` (owner create endpoint), `server/MCPal.Server.Web/ServerWebServices.cs` (rate limit policy), `server/MCPal.Server/Program.cs` (map endpoint).
- Tests: `server/MCPal.Server.Tests/Tenancy/BridgeEnrollmentServiceTests.cs`, `server/MCPal.Server.Tests/Portal/BridgeEnrollmentEndpointTests.cs`, `server/MCPal.Server.Tests/McpalOptionsTests.cs`.

Bridge (all under `bridge/MCPal.Bridge/`):
- Create `Enrollment/BridgeCredentials.cs`, `Enrollment/BridgeEnroller.cs`, `Enrollment/EnrollCommand.cs`.
- Modify `Config/BridgeConfigLoader.cs` (stored key, `credentialsFile`, `LoadEnrollmentTarget`), `BridgeCommandLine.cs` (verb), `Program.cs`.
- Tests under `bridge/MCPal.Bridge.Tests/`: `Enrollment/BridgeCredentialsTests.cs`, `Enrollment/BridgeEnrollerTests.cs`, `Enrollment/EnrollCommandTests.cs`, `Config/BridgeConfigCredentialsTests.cs`, `BridgeCommandLineTests.cs`.

Packaging: `bridge/packaging/linux/install.sh`, `bridge/packaging/linux/test-install.sh`, `bridge/packaging/windows/install.ps1`, `bridge/packaging/docker/mcpal.docker.json`, `bridge/packaging/docker/compose.yml`, `bridge/packaging/docker/README.md`.

SPA: `server/portal/src/api/types.ts`, `api/client.ts`, `i18n/en.ts`, `pages/SetupPage.tsx`, `pages/SetupPage.test.tsx`.

E2E: `tests/MCPal.E2E.Tests/EnrollmentE2ETests.cs`.

Docs: `README.md`, `bridge/packaging/README.linux.txt`, `bridge/packaging/README.windows.txt`, `docs/tunnel-protocol.md`, `docs/next-steps.md`, `CHANGELOG.md`, `plan.md`, spec touch-ups.

---

### Task 1: Entity, option and migration

**Files:**
- Modify: `server/MCPal.Server.Domain/Tenancy/Entities.cs` (append class)
- Modify: `server/MCPal.Server.Domain/McpalOptions.cs`
- Modify: `server/MCPal.Server.Storage/MCPalDbContext.cs`
- Create (generated): `server/MCPal.Server.Storage/Migrations/<timestamp>_BridgeEnrollments.cs` and `.Designer.cs`, updated `MCPalDbContextModelSnapshot.cs`
- Test: `server/MCPal.Server.Tests/McpalOptionsTests.cs`

**Interfaces:**
- Produces: `BridgeEnrollment` entity (namespace `MCPal.Server.Tenancy`) with `Id`, `CompanyId`, `CodeHash`, `CreatedByUserId`, `CreatedAt`, `ExpiresAt`, `RedeemedAt`, `ApiKeyId`; `MCPalDbContext.BridgeEnrollments`; `McpalOptions.EnrollmentLifetimeMinutes` (int, 1..1440, default 15).

- [ ] **Step 1: Write the failing options test**

Add to `McpalOptionsTests.cs` (inside the class; needs `using System.ComponentModel.DataAnnotations;`, already present):

```csharp
    [TestCase(0)]
    [TestCase(1441)]
    public void EnrollmentLifetimeMinutes_OutsideOneToOneThousandFourHundredForty_FailsAnnotationValidation(int minutes)
    {
        var options = new McpalOptions { EnrollmentLifetimeMinutes = minutes };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle().Which.MemberNames.Should().Contain(nameof(McpalOptions.EnrollmentLifetimeMinutes));
    }

    [Test]
    public void EnrollmentLifetimeMinutes_Default_IsFifteen()
    {
        new McpalOptions().EnrollmentLifetimeMinutes.Should().Be(15);
    }
```

- [ ] **Step 2: Run it to verify it fails (compile error)**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~McpalOptionsTests" 2>&1 | tail -15`
Expected: build FAIL, `'McpalOptions' does not contain a definition for 'EnrollmentLifetimeMinutes'`.

- [ ] **Step 3: Add the option**

In `McpalOptions.cs`, after `BridgeImage`:

```csharp
    /// <summary>How long a bridge enrollment code (created on the Setup page) stays valid, in minutes.</summary>
    [Range(1, 1440)]
    public int EnrollmentLifetimeMinutes { get; set; } = 15;
```

- [ ] **Step 4: Add the entity**

Append to `server/MCPal.Server.Domain/Tenancy/Entities.cs` (inside namespace `MCPal.Server.Tenancy`, after `ApiKey`):

```csharp
/// <summary>
/// A single-use code with which a bridge fetches its own bridge key. The code is stored hashed and shown once, to the owner who creates it.
/// </summary>
internal sealed class BridgeEnrollment
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>SHA-256 of the code, hex encoded.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>The owner who created the code; the key made on redemption lists this user as its creator.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The bridge key created on redemption.</summary>
    public Guid? ApiKeyId { get; set; }
}
```

- [ ] **Step 5: Map it**

In `MCPalDbContext.cs` add the set after `Invitations`:

```csharp
    public DbSet<BridgeEnrollment> BridgeEnrollments => Set<BridgeEnrollment>();
```

and the mapping after the `Invitation` block in `OnModelCreating`:

```csharp
        builder.Entity<BridgeEnrollment>(enrollment =>
        {
            enrollment.HasKey(e => e.Id);
            enrollment.Property(e => e.CodeHash).HasMaxLength(64);
            enrollment.Property(e => e.CreatedByUserId).HasMaxLength(450);
            enrollment.HasIndex(e => e.CodeHash).IsUnique();
            enrollment.HasIndex(e => e.CompanyId);
            enrollment.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 6: Generate the migration**

Run: `cd /home/jabba/MCPal && dotnet ef migrations add BridgeEnrollments --project server/MCPal.Server.Storage --output-dir Migrations 2>&1 | tail -5`
Expected: "Done." and a new `*_BridgeEnrollments.cs`. Open it: it must only `CreateTable("BridgeEnrollments", …)` plus two indexes and the FK to `Companies`; `Down` drops the table. If it contains anything else, the model snapshot was out of sync: stop and report.

- [ ] **Step 7: Run tests and build**

Run: `cd /home/jabba/MCPal && dotnet build MCPal.slnx 2>&1 | tail -3 && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~McpalOptionsTests" 2>&1 | tail -5`
Expected: 0 warnings, tests PASS.

- [ ] **Step 8: Commit**

```bash
cd /home/jabba/MCPal && git add server && git commit -m "feat: BridgeEnrollment entity, option and migration" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 2: BridgeEnrollmentService

**Files:**
- Create: `server/MCPal.Server.Application/Tenancy/BridgeEnrollmentService.cs`
- Modify: `server/MCPal.Server.Application/Tenancy/ApiKeyService.cs` (make `Alphabet` internal)
- Modify: `server/MCPal.Server.Application/ApplicationModule.cs`
- Test: `server/MCPal.Server.Tests/Tenancy/BridgeEnrollmentServiceTests.cs`

**Interfaces:**
- Consumes: `IMcpalData`, `IApiKeyService.CreateAsync(Guid, NewApiKey, CancellationToken)`, `NewApiKey.Bridge(string name, string? createdByUserId, DateTimeOffset? expiresAt)`, `ApiKeyService.Hash(string)`, `ApiKeyQueries.Active(users, companies)`, `McpalOptions.EnrollmentLifetimeMinutes`.
- Produces (namespace `MCPal.Server.Tenancy`):
  - `record CreatedEnrollment(string Code, DateTimeOffset ExpiresAt)`
  - `record RedeemedEnrollment(string ApiKey)`
  - `BridgeEnrollmentService.CreateAsync(Guid companyId, string userId, CancellationToken)` returns `CreatedEnrollment?` (null when the company already has 10 open codes)
  - `BridgeEnrollmentService.RedeemAsync(string? code, string? bridgeName, CancellationToken)` returns `RedeemedEnrollment?` (null for every failure)
  - constants `BridgeEnrollmentService.CodePrefix` (`"mcpale_"`), `MaxOpenCodes` (10)

- [ ] **Step 1: Write the failing tests**

Create `server/MCPal.Server.Tests/Tenancy/BridgeEnrollmentServiceTests.cs`:

```csharp
using Autofac;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Tenancy;

[TestFixture]
internal sealed class BridgeEnrollmentServiceTests : ServerTestBase
{
    private static async Task<CreatedEnrollment> NewCodeAsync(BridgeEnrollmentService service, Guid companyId, string ownerId) =>
        await service.CreateAsync(companyId, ownerId, Ct) ?? throw new InvalidOperationException("No code created.");

    private static async Task<(Guid CompanyId, string OwnerId)> SeedAsync(ILifetimeScope scope, string name = "Acme", string email = "owner@acme.example")
    {
        var company = await scope.Resolve<ICompanyService>().CreateAsync(name, Ct);
        var owner = await CreateUserAsync(scope, company.Id, email, PortalRole.Owner);
        return (company.Id, owner.Id);
    }

    [Test]
    public async Task CreateAsync_Owner_ReturnsCodeWithPrefixAndExpiry()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var now = scope.Resolve<FakeTimeProvider>().GetUtcNow();

        var created = await scope.Resolve<BridgeEnrollmentService>().CreateAsync(companyId, ownerId, Ct);

        created.Should().NotBeNull();
        created.Code.Should().MatchRegex("^mcpale_[A-Za-z0-9]{24}$");
        created.ExpiresAt.Should().Be(now.AddMinutes(15));
    }

    [Test]
    public async Task CreateAsync_NewCode_StoresOnlyHash()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);

        var created = await NewCodeAsync(scope.Resolve<BridgeEnrollmentService>(), companyId, ownerId);

        var stored = await scope.Resolve<MCPalDbContext>().BridgeEnrollments.AsNoTracking().SingleAsync(Ct);
        stored.CodeHash.Should().Be(ApiKeyService.Hash(created.Code)).And.NotContain(created.Code);
        stored.CompanyId.Should().Be(companyId);
        stored.CreatedByUserId.Should().Be(ownerId);
        stored.RedeemedAt.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_ElevenOpenCodes_RefusesTheEleventh()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            (await service.CreateAsync(companyId, ownerId, Ct)).Should().NotBeNull();
        }

        var eleventh = await service.CreateAsync(companyId, ownerId, Ct);

        eleventh.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_OtherCompanyHasTenOpenCodes_IsNotAffected()
    {
        await using var scope = await GetServicesAsync();
        var (acme, acmeOwner) = await SeedAsync(scope);
        var (globex, globexOwner) = await SeedAsync(scope, "Globex", "owner@globex.example");
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await service.CreateAsync(acme, acmeOwner, Ct);
        }

        (await service.CreateAsync(globex, globexOwner, Ct)).Should().NotBeNull();
    }

    [Test]
    public async Task CreateAsync_OldCodesExpired_DeletesThemAndAllowsNewOnes()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await service.CreateAsync(companyId, ownerId, Ct);
        }

        scope.Resolve<FakeTimeProvider>().Advance(TimeSpan.FromMinutes(16));
        var created = await NewCodeAsync(service, companyId, ownerId);

        created.Should().NotBeNull();
        (await scope.Resolve<MCPalDbContext>().BridgeEnrollments.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_ValidCode_CreatesBridgeKeyOfTheCompany()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        var redeemed = await service.RedeemAsync(created.Code, "hq-01", Ct);

        redeemed.Should().NotBeNull();
        var validated = await scope.Resolve<IApiKeyService>().ValidateAsync(redeemed.ApiKey, Ct);
        validated.Should().NotBeNull();
        validated.CompanyId.Should().Be(companyId);
        validated.Purpose.Should().Be(ApiKeyPurpose.Bridge);
        var db = scope.Resolve<MCPalDbContext>();
        var key = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == validated.ApiKeyId, Ct);
        key.Name.Should().Be("Bridge hq-01");
        key.CreatedByUserId.Should().Be(ownerId);
        var enrollment = await db.BridgeEnrollments.AsNoTracking().SingleAsync(Ct);
        enrollment.RedeemedAt.Should().NotBeNull();
        enrollment.ApiKeyId.Should().Be(key.Id);
    }

    [Test]
    public async Task RedeemAsync_SecondTime_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await service.RedeemAsync(created.Code, "hq-01", Ct);

        var second = await service.RedeemAsync(created.Code, "hq-02", Ct);

        second.Should().BeNull();
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_Expired_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        scope.Resolve<FakeTimeProvider>().Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();
    }

    [Test]
    public async Task RedeemAsync_TwoAtTheSameTime_CreateExactlyOneKey()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var created = await NewCodeAsync(scope.Resolve<BridgeEnrollmentService>(), companyId, ownerId);
        await using var first = scope.BeginLifetimeScope();
        await using var second = scope.BeginLifetimeScope();

        var results = await Task.WhenAll(
            first.Resolve<BridgeEnrollmentService>().RedeemAsync(created.Code, "a", Ct),
            second.Resolve<BridgeEnrollmentService>().RedeemAsync(created.Code, "b", Ct));

        results.Count(r => r is not null).Should().Be(1);
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_CodeOfCompanyA_KeyBelongsToCompanyAOnly()
    {
        await using var scope = await GetServicesAsync();
        var (acme, acmeOwner) = await SeedAsync(scope);
        var (globex, _) = await SeedAsync(scope, "Globex", "owner@globex.example");
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await service.CreateAsync(acme, acmeOwner, Ct);

        var redeemed = await service.RedeemAsync(created.Code, "hq-01", Ct);

        redeemed.Should().NotBeNull();
        var validated = await scope.Resolve<IApiKeyService>().ValidateAsync(redeemed.ApiKey, Ct);
        validated.Should().NotBeNull();
        validated.CompanyId.Should().Be(acme);
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(k => k.CompanyId == globex, Ct)).Should().Be(0);
    }

    [Test]
    public async Task RedeemAsync_CreatorDisabledAfterwards_ReturnsNullAndCreatesNoKey()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await scope.Resolve<MCPalDbContext>().Users.Where(u => u.Id == ownerId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true), Ct);

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(0);
    }

    [Test]
    public async Task RedeemAsync_CompanyDisabledAfterwards_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await scope.Resolve<MCPalDbContext>().Companies.Where(c => c.Id == companyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Disabled, true), Ct);

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("garbage")]
    [TestCase("mcpale_neverIssuedNeverIssued1")]
    [TestCase("mcpal_0123abcd_wrongPrefixWrongPrefixWrongPrefixWrong")]
    public async Task RedeemAsync_UnknownOrMalformedCode_ReturnsNull(string? code)
    {
        await using var scope = await GetServicesAsync();
        await SeedAsync(scope);

        (await scope.Resolve<BridgeEnrollmentService>().RedeemAsync(code, "hq-01", Ct)).Should().BeNull();
    }

    [TestCase(null, "Bridge bridge")]
    [TestCase("", "Bridge bridge")]
    [TestCase("  hq 01  ", "Bridge hq 01")]
    [TestCase("a\nb\u0007c", "Bridge abc")]
    public async Task RedeemAsync_BridgeName_IsCleanedForTheKeyName(string? bridgeName, string expectedKeyName)
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        await service.RedeemAsync(created.Code, bridgeName, Ct);

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name.Should().Be(expectedKeyName);
    }

    [Test]
    public async Task RedeemAsync_VeryLongBridgeName_IsTruncated()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        await service.RedeemAsync(created.Code, new string('x', 500), Ct);

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name.Should().HaveLength("Bridge ".Length + 100);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~BridgeEnrollmentServiceTests" 2>&1 | tail -15`
Expected: build FAIL, `The type or namespace name 'BridgeEnrollmentService' could not be found`.

- [ ] **Step 3: Expose the key alphabet**

In `ApiKeyService.cs` change the last line `private const string Alphabet = …` to `internal const string Alphabet = …` (same value).

- [ ] **Step 4: Implement the service**

Create `server/MCPal.Server.Application/Tenancy/BridgeEnrollmentService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using MCPal.Server.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Tenancy;

internal sealed record CreatedEnrollment(string Code, DateTimeOffset ExpiresAt);

internal sealed record RedeemedEnrollment(string ApiKey);

/// <summary>
/// Single-use enrollment codes: an owner creates one on the Setup page, a bridge trades it for its own bridge key. The code is stored
/// hashed. The company comes from the code, never from the caller, so a code can only produce a key for the company that made it.
/// </summary>
internal sealed class BridgeEnrollmentService(
    IMcpalData db,
    IApiKeyService apiKeys,
    TimeProvider timeProvider,
    IOptions<McpalOptions> options,
    ILogger<BridgeEnrollmentService> logger)
{
    public const string CodePrefix = "mcpale_";

    /// <summary>Unredeemed, unexpired codes one company may hold. A soft limit: two requests at the same instant can both pass it.</summary>
    public const int MaxOpenCodes = 10;

    private const int CodeLength = 24;
    private const int MaxCodeLength = 64;
    private const int MaxBridgeNameLength = 100;

    /// <returns>The new code, or null when the company already holds <see cref="MaxOpenCodes"/> open codes.</returns>
    public async Task<CreatedEnrollment?> CreateAsync(Guid companyId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = timeProvider.GetUtcNow();
        await db.Query<BridgeEnrollment>().Where(e => e.CompanyId == companyId && e.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var open = await db.Query<BridgeEnrollment>().AsNoTracking()
            .CountAsync(e => e.CompanyId == companyId && e.RedeemedAt == null && e.ExpiresAt > now, cancellationToken);
        if (open >= MaxOpenCodes)
        {
            return null;
        }

        var code = CodePrefix + RandomNumberGenerator.GetString(ApiKeyService.Alphabet, CodeLength);
        var expiresAt = now.AddMinutes(options.Value.EnrollmentLifetimeMinutes);
        db.Add(new BridgeEnrollment
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            CodeHash = ApiKeyService.Hash(code),
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedEnrollment(code, expiresAt);
    }

    /// <returns>The raw bridge key, or null when the code is unknown, used, expired or its creator or company is no longer active. The caller cannot tell which.</returns>
    public async Task<RedeemedEnrollment?> RedeemAsync(string? code, string? bridgeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength || !code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            logger.LogInformation("Bridge enrollment rejected: malformed code");
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hash = ApiKeyService.Hash(code);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);

        // The conditional update is the claim: of two requests with the same code only one changes a row (the other waits for the row lock and then no longer matches).
        var claimed = await db.Query<BridgeEnrollment>()
            .Where(e => e.CodeHash == hash && e.RedeemedAt == null && e.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.RedeemedAt, now), cancellationToken);
        if (claimed == 0)
        {
            logger.LogInformation("Bridge enrollment rejected: unknown, used or expired code");
            return null;
        }

        var enrollment = await db.Query<BridgeEnrollment>().AsNoTracking().SingleAsync(e => e.CodeHash == hash, cancellationToken);
        var creatorActive = await db.Query<PortalUser>().AsNoTracking()
            .Active(db.Query<Company>())
            .AnyAsync(u => u.Id == enrollment.CreatedByUserId && u.CompanyId == enrollment.CompanyId, cancellationToken);
        if (!creatorActive)
        {
            // Dispose without commit rolls the claim back.
            logger.LogInformation("Bridge enrollment rejected: creator or company of {EnrollmentId} is not active", enrollment.Id);
            return null;
        }

        var created = await apiKeys.CreateAsync(enrollment.CompanyId, NewApiKey.Bridge($"Bridge {CleanName(bridgeName)}", enrollment.CreatedByUserId), cancellationToken);
        await db.Query<BridgeEnrollment>().Where(e => e.Id == enrollment.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.ApiKeyId, created.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bridge enrollment {EnrollmentId} redeemed for company {CompanyId}", enrollment.Id, enrollment.CompanyId);
        return new RedeemedEnrollment(created.RawKey);
    }

    private static string CleanName(string? bridgeName)
    {
        var builder = new StringBuilder();
        foreach (var c in bridgeName ?? string.Empty)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var name = builder.ToString().Trim();
        if (name.Length > MaxBridgeNameLength)
        {
            name = name[..MaxBridgeNameLength].TrimEnd();
        }

        return name.Length == 0 ? "bridge" : name;
    }
}
```

Note: the truncation test expects length `7 + 100` for `new string('x', 500)`; `TrimEnd` does not shorten a run of `x`.

- [ ] **Step 5: Register it**

In `ApplicationModule.cs` after the `TeamService` line:

```csharp
        builder.RegisterType<BridgeEnrollmentService>().AsSelf().InstancePerLifetimeScope();
```

- [ ] **Step 6: Run the tests**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~BridgeEnrollmentServiceTests" 2>&1 | tail -15`
Expected: all PASS (needs Docker). If `RedeemAsync_TwoAtTheSameTime…` fails because the two child scopes share one `MCPalDbContext`, check how `StorageModule` registers the context (it must be `InstancePerLifetimeScope`); fix the test to resolve per scope, not the registration.

- [ ] **Step 7: Commit**

```bash
cd /home/jabba/MCPal && git add server && git commit -m "feat: BridgeEnrollmentService creates and redeems enrollment codes" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 3: Server endpoints

**Files:**
- Create: `server/MCPal.Server.Web/Tenancy/BridgeEnrollEndpoints.cs`
- Modify: `server/MCPal.Server.Web/Portal/SetupEndpoints.cs`
- Modify: `server/MCPal.Server.Web/ServerWebServices.cs`
- Modify: `server/MCPal.Server/Program.cs`
- Test: `server/MCPal.Server.Tests/Portal/BridgeEnrollmentEndpointTests.cs`

**Interfaces:**
- Consumes: `BridgeEnrollmentService.CreateAsync/RedeemAsync` (Task 2), `PortalEndpoints.Problems(IEnumerable<string>, int)`, `OwnerOnlyFilter`.
- Produces:
  - `POST /api/portal/setup/enrollments` (owner, anti-forgery): `201 {"code":"mcpale_…","expiresAt":"…"}`, or `409 {"errors":["…"]}`.
  - `POST /api/bridge/enroll` (anonymous): body `{"code","bridgeName"}`; `200 {"url","apiKey"}` or `400 {"error":"invalid_code"}`; `429` over the rate limit.
  - `BridgeEnrollEndpoints.RateLimitPolicy` = `"enroll"`, 20 requests per minute per IP.

- [ ] **Step 1: Write the failing tests**

Create `server/MCPal.Server.Tests/Portal/BridgeEnrollmentEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class BridgeEnrollmentEndpointTests
{
    private const string PublicUrl = "https://mcpal.example.com";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static Dictionary<string, string?> Settings() => new() { ["Mcpal:PublicUrl"] = PublicUrl };

    private static async Task<PortalClient> SignedUpAsync(ServerWebApplicationFactory factory, string company = "Acme", string email = "admin@acme.example")
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return portal;
    }

    private static async Task<string> CreateCodeAsync(PortalClient portal)
    {
        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await PortalClient.JsonAsync(response, Ct)).GetProperty("code").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> EnrollAsync(HttpClient client, string? code, string bridgeName = "hq-01") =>
        await client.PostAsJsonAsync("/api/bridge/enroll", new { code, bridgeName }, Ct);

    [Test]
    public async Task CreateEnrollment_Owner_ReturnsCodeAndExpiry()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await PortalClient.JsonAsync(response, Ct);
        body.GetProperty("code").GetString().Should().StartWith("mcpale_");
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task CreateEnrollment_Member_IsForbidden()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        var company = await factory.SeedCompanyAsync("Globex", Ct);
        await factory.SeedUserAsync(company.CompanyId, "member@globex.example", PortalRole.Member, Ct);
        using var member = new PortalClient(factory);
        using var login = await member.PostAsync("/api/portal/auth/login", new { email = "member@globex.example", password = PortalClient.Password }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await member.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateEnrollment_NotSignedIn_IsUnauthorized()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = new PortalClient(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CreateEnrollment_WithoutAntiforgeryToken_IsRejected()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateEnrollment_EleventhOpenCode_IsConflict()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await CreateCodeAsync(portal);
        }

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors")[0].GetString().Should().Contain("unused enrollment codes");
    }

    [Test]
    public async Task Enroll_ValidCode_ReturnsPublicUrlAndAKeyThatOpensTheTunnel()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var code = await CreateCodeAsync(portal);
        using var anonymous = factory.CreateClient();

        using var response = await EnrollAsync(anonymous, code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await PortalClient.JsonAsync(response, Ct);
        body.GetProperty("url").GetString().Should().Be(PublicUrl);
        var apiKey = body.GetProperty("apiKey").GetString() ?? string.Empty;
        apiKey.Should().StartWith("mcpal_");
        await using var bridge = await FakeBridge.StartAsync(factory, apiKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        using var setup = await portal.GetAsync("/api/portal/setup", Ct);
        (await PortalClient.JsonAsync(setup, Ct)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Enroll_ValidCode_KeyAppearsInTheKeyListOfTheCodesCompanyOnly()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var acme = await SignedUpAsync(factory);
        using var globex = await SignedUpAsync(factory, "Globex", "admin@globex.example");
        var code = await CreateCodeAsync(acme);
        using var anonymous = factory.CreateClient();
        (await EnrollAsync(anonymous, code, "hq-01")).Dispose();

        using var acmeKeys = await acme.GetAsync("/api/portal/keys", Ct);
        using var globexKeys = await globex.GetAsync("/api/portal/keys", Ct);

        (await PortalClient.JsonAsync(acmeKeys, Ct)).EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().Contain("Bridge hq-01");
        (await PortalClient.JsonAsync(globexKeys, Ct)).EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().NotContain("Bridge hq-01");
    }

    [Test]
    public async Task Enroll_SameCodeTwice_SecondIsInvalidCode()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var code = await CreateCodeAsync(portal);
        using var anonymous = factory.CreateClient();
        (await EnrollAsync(anonymous, code)).Dispose();

        using var second = await EnrollAsync(anonymous, code);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(second, Ct)).GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [TestCase("mcpale_unknownunknownunknown1")]
    [TestCase("")]
    [TestCase(null)]
    public async Task Enroll_BadCode_IsInvalidCodeJson(string? code)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();

        using var response = await EnrollAsync(anonymous, code);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [TestCase("")]
    [TestCase("not json")]
    public async Task Enroll_EmptyOrInvalidBody_Is400NotHtmlAndNotAServerError(string body)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync("/api/bridge/enroll", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Test]
    public async Task Enroll_MoreThanTwentyRequestsPerMinute_IsRateLimited()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 25; i++)
        {
            using var response = await EnrollAsync(anonymous, "mcpale_unknownunknownunknown1");
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses.Take(20).Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
    }
}
```

`JsonElement` import (`System.Text.Json`) is used by `PortalClient.JsonAsync`; if the compiler reports an unused using, drop it.

- [ ] **Step 2: Run to verify it fails**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~BridgeEnrollmentEndpointTests" 2>&1 | tail -15`
Expected: tests FAIL with 404 / 405 (endpoints do not exist; the portal POST falls to the SPA fallback or 404).

- [ ] **Step 3: Add the anonymous endpoint**

Create `server/MCPal.Server.Web/Tenancy/BridgeEnrollEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace MCPal.Server.Tenancy;

internal sealed record EnrollRequest(string? Code, string? BridgeName);

internal sealed record EnrollResponse(string Url, string ApiKey);

/// <summary>
/// Anonymous: the enrollment code is the credential. A bridge trades it for its own bridge key. It sits outside <c>/api/portal</c>
/// because no cookie, user or anti-forgery token is involved; the rate limit policy slows guessing and creation spam.
/// </summary>
internal static class BridgeEnrollEndpoints
{
    public const string RateLimitPolicy = "enroll";

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/bridge/enroll", EnrollAsync).RequireRateLimiting(RateLimitPolicy);
    }

    private static async Task<IResult> EnrollAsync(EnrollRequest? request, BridgeEnrollmentService enrollments, IOptions<McpalOptions> options, CancellationToken cancellationToken)
    {
        var redeemed = await enrollments.RedeemAsync(request?.Code, request?.BridgeName, cancellationToken);
        return redeemed is null
            ? Results.Json(new { error = "invalid_code" }, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(new EnrollResponse(options.Value.PublicUrl.TrimEnd('/'), redeemed.ApiKey));
    }
}
```

- [ ] **Step 4: Add the owner endpoint**

In `SetupEndpoints.cs` add `using System.Security.Claims;` is not needed (fully qualified is used); add the record near the other records:

```csharp
internal sealed record EnrollmentResponse(string Code, DateTimeOffset ExpiresAt);
```

Change `Map` to:

```csharp
    public static void Map(RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);

        var owners = secured.MapGroup(string.Empty).AddEndpointFilter<OwnerOnlyFilter>();
        owners.MapGet("setup", SetupAsync);
        owners.MapPost("setup/enrollments", CreateEnrollmentAsync);
    }

    private static async Task<IResult> CreateEnrollmentAsync(System.Security.Claims.ClaimsPrincipal principal, UserManager<PortalUser> users, BridgeEnrollmentService enrollments, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return Results.Unauthorized();
        }

        return await enrollments.CreateAsync(user.CompanyId, user.Id, cancellationToken) is { } created
            ? Results.Json(new EnrollmentResponse(created.Code, created.ExpiresAt), statusCode: StatusCodes.Status201Created)
            : PortalEndpoints.Problems(["Too many unused enrollment codes. Use one or wait until it expires, then try again."], StatusCodes.Status409Conflict);
    }
```

- [ ] **Step 5: Rate limit policy and mapping**

In `ServerWebServices.cs` after the `PortalEndpoints.RateLimitPolicy` policy:

```csharp
            options.AddPolicy(BridgeEnrollEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + context.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
```

In `server/MCPal.Server/Program.cs` after `PortalEndpoints.Map(app);`:

```csharp
BridgeEnrollEndpoints.Map(app);
```

(`using MCPal.Server.Tenancy;` is already there.)

- [ ] **Step 6: Run the tests**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~BridgeEnrollmentEndpointTests" 2>&1 | tail -15`
Expected: all PASS. If `Enroll_EmptyOrInvalidBody…` gets 415 or 500 instead of 400, fix the handler signature (bind with `HttpRequest` and parse manually inside try/catch `JsonException`, answering the same `invalid_code` 400) rather than changing the test.

- [ ] **Step 7: Run the whole server suite (a ring module and `Program.cs` changed)**

Run: `cd /home/jabba/MCPal && dotnet test server/MCPal.Server.Tests 2>&1 | tail -8`
Expected: all PASS (Architecture tests included: Web must not reference Storage).

- [ ] **Step 8: Commit**

```bash
cd /home/jabba/MCPal && git add server && git commit -m "feat: enrollment endpoints for owners and bridges" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 4: Bridge credentials file and config loading

**Files:**
- Create: `bridge/MCPal.Bridge/Enrollment/BridgeCredentials.cs`
- Modify: `bridge/MCPal.Bridge/Config/BridgeConfigLoader.cs`
- Test: `bridge/MCPal.Bridge.Tests/Enrollment/BridgeCredentialsTests.cs`, `bridge/MCPal.Bridge.Tests/Config/BridgeConfigCredentialsTests.cs`

**Interfaces:**
- Consumes: `BridgeConfigException` (namespace `MCPal.Bridge.Config`).
- Produces:
  - `BridgeCredentials.DefaultFileName` (`"credentials.json"`), `string? BridgeCredentials.TryRead(string path)`, `void BridgeCredentials.Write(string path, string apiKey)` (mode 600 on Unix, creates the directory).
  - `record EnrollmentTarget(string? Url, string BridgeName, string CredentialsPath, bool HasKey)` in `MCPal.Bridge.Config`.
  - `BridgeConfigLoader.LoadEnrollmentTarget(string path, IReadOnlyDictionary<string,string?> environment, string? urlOverride)`.
  - Config option `mcpal.credentialsFile` (relative to the config file). `BridgeConfigLoader.Load` uses the stored key when neither `MCPAL_API_KEY` nor `mcpal.apiKey` is set.

- [ ] **Step 1: Write the failing credentials tests**

Create `bridge/MCPal.Bridge.Tests/Enrollment/BridgeCredentialsTests.cs`:

```csharp
using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class BridgeCredentialsTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public void WriteThenTryRead_RoundTripsTheKey()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            BridgeCredentials.TryRead(path).Should().Be("mcpal_aaaaaaaa_secret");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Write_MissingDirectory_CreatesIt()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "sub", "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Write_NewFile_HasModeSixHundred()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "File modes are Unix only; Windows relies on the ACL of the data directory.");
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Write_ExistingFileWithWiderMode_NarrowsItToSixHundred()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "File modes are Unix only.");
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TryRead_MissingFile_ReturnsNull()
    {
        BridgeCredentials.TryRead(Path.Combine(Path.GetTempPath(), "mcpal-missing-" + Guid.NewGuid().ToString("N"), "credentials.json")).Should().BeNull();
    }

    [Test]
    public void TryRead_FileWithoutKey_ReturnsNull()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "{ \"apiKey\": \"  \" }");

            BridgeCredentials.TryRead(path).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TryRead_InvalidJson_ThrowsConfigExceptionNamingThePath()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "not json");

            var act = () => BridgeCredentials.TryRead(path);

            act.Should().Throw<BridgeConfigException>().WithMessage($"*{path}*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Write the failing loader tests**

Create `bridge/MCPal.Bridge.Tests/Config/BridgeConfigCredentialsTests.cs`:

```csharp
using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Config;

[TestFixture]
internal sealed class BridgeConfigCredentialsTests
{
    private const string UrlOnly = """{ "mcpal": { "url": "https://mcpal.example.com" } }""";

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteConfig(string directory, string json)
    {
        var path = Path.Combine(directory, "mcpal.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public void Load_NoKeyInConfigOrEnvironment_UsesTheCredentialsFileNextToTheConfig()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_EnvironmentAndStoredKey_EnvironmentWins()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var environment = new Dictionary<string, string?> { ["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env" };

            BridgeConfigLoader.Load(config, environment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_bbbbbbbb_env");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_EmptyEnvironmentKeyAndStoredKey_UsesTheStoredKey()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var environment = new Dictionary<string, string?> { ["MCPAL_API_KEY"] = string.Empty };

            BridgeConfigLoader.Load(config, environment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_ConfigKeyAndStoredKey_ConfigWins()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "mcpal_cccccccc_config" } }""");
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_cccccccc_config");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_CredentialsFileOption_IsRelativeToTheConfig()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "https://mcpal.example.com", "credentialsFile": "state/keys.json" } }""");
            BridgeCredentials.Write(Path.Combine(directory, "state", "keys.json"), "mcpal_aaaaaaaa_elsewhere");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_elsewhere");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_NoKeyAnywhere_MessageNamesTheEnrollmentVariable()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);

            var act = () => BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true);

            act.Should().Throw<BridgeConfigException>().WithMessage("*MCPAL_API_KEY*MCPAL_ENROLL*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_UrlWithVariable_IsExpanded()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "${MCPAL_URL}", "bridgeName": "hq-01" } }""");
            var environment = new Dictionary<string, string?> { ["MCPAL_URL"] = "https://mcpal.example.com" };

            var target = BridgeConfigLoader.LoadEnrollmentTarget(config, environment, urlOverride: null);

            target.Url.Should().Be("https://mcpal.example.com");
            target.BridgeName.Should().Be("hq-01");
            target.CredentialsPath.Should().Be(Path.Combine(directory, "credentials.json"));
            target.HasKey.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_UrlOverride_SkipsExpansionOfTheConfiguredUrl()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "${MCPAL_URL_NOT_SET}" } }""");

            var target = BridgeConfigLoader.LoadEnrollmentTarget(config, NoEnvironment, urlOverride: "https://given.example.com");

            target.Url.Should().Be("https://given.example.com");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_NoUrlAnywhere_ReturnsNullUrl()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, "{}");

            BridgeConfigLoader.LoadEnrollmentTarget(config, NoEnvironment, urlOverride: null).Url.Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("env")]
    [TestCase("config")]
    [TestCase("file")]
    public void LoadEnrollmentTarget_KeyFromAnySource_HasKey(string source)
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, source == "config"
                ? """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "mcpal_cccccccc_config" } }"""
                : UrlOnly);
            if (source == "file")
            {
                BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            }

            var environment = new Dictionary<string, string?>();
            if (source == "env")
            {
                environment["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env";
            }

            BridgeConfigLoader.LoadEnrollmentTarget(config, environment, urlOverride: null).HasKey.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `cd /home/jabba/MCPal && dotnet test bridge/MCPal.Bridge.Tests --filter "FullyQualifiedName~Credentials" 2>&1 | tail -15`
Expected: build FAIL (`BridgeCredentials`, `LoadEnrollmentTarget` missing).

- [ ] **Step 4: Implement `BridgeCredentials`**

Create `bridge/MCPal.Bridge/Enrollment/BridgeCredentials.cs`:

```csharp
using System.Text.Json;
using MCPal.Bridge.Config;

namespace MCPal.Bridge.Enrollment;

/// <summary>
/// The bridge key a bridge got by enrolling, kept in a small JSON file next to <c>mcpal.json</c> (or at <c>mcpal.credentialsFile</c>).
/// On Unix the file is mode 600; on Windows the ACL of the data directory (set by <c>install.ps1</c>) protects it.
/// </summary>
internal static class BridgeCredentials
{
    public const string DefaultFileName = "credentials.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The stored key, or null when the file does not exist or holds none.</summary>
    public static string? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeConfigException($"Cannot read credentials file '{path}': {ex.Message}");
        }

        try
        {
            var key = JsonSerializer.Deserialize<StoredCredentials>(content, JsonOptions)?.ApiKey;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (JsonException ex)
        {
            throw new BridgeConfigException($"Credentials file '{path}' is not valid: {ex.Message}");
        }
    }

    public static void Write(string path, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(path, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(new StoredCredentials(apiKey), JsonOptions));
            }

            if (!OperatingSystem.IsWindows())
            {
                // UnixCreateMode applies to new files only; an older, wider file is narrowed here.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeConfigException($"Cannot write credentials file '{path}': {ex.Message}");
        }
    }

    private sealed record StoredCredentials(string? ApiKey);
}
```

- [ ] **Step 5: Extend the loader**

In `BridgeConfigLoader.cs`:

1. Add `using MCPal.Bridge.Enrollment;` at the top.
2. Replace `Load`:

```csharp
    public static BridgeConfig Load(string path, IReadOnlyDictionary<string, string?> environment, bool requireMcpal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);

        var raw = ParseRaw(ReadFile(path, "config file"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var stored = requireMcpal ? BridgeCredentials.TryRead(CredentialsPath(raw.Mcpal, directory)) : null;
        return Build(raw, ReadServersFile(raw, directory), environment, requireMcpal, stored);
    }

    /// <summary>
    /// What <c>enroll</c> needs: the server URL (<paramref name="urlOverride"/>, else <c>mcpal.url</c> with variables expanded; null when neither
    /// is set), the bridge name, where the key goes and whether the bridge has a key already (environment, config or credentials file).
    /// </summary>
    public static EnrollmentTarget LoadEnrollmentTarget(string path, IReadOnlyDictionary<string, string?> environment, string? urlOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);

        var raw = ParseRaw(ReadFile(path, "config file"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var mcpal = raw.Mcpal;
        var credentialsPath = CredentialsPath(mcpal, directory);

        string? url = null;
        if (!string.IsNullOrWhiteSpace(urlOverride))
        {
            url = urlOverride.Trim();
        }
        else if (mcpal?.Url is { } rawUrl && !string.IsNullOrWhiteSpace(rawUrl))
        {
            url = EnvironmentExpander.Expand(rawUrl, environment, "Config section 'mcpal'");
        }

        var hasKey = !string.IsNullOrWhiteSpace(environment.GetValueOrDefault(ApiKeyVariable))
            || !string.IsNullOrWhiteSpace(mcpal?.ApiKey)
            || BridgeCredentials.TryRead(credentialsPath) is not null;
        var name = mcpal?.BridgeName is { } configured && !string.IsNullOrWhiteSpace(configured) ? configured : Environment.MachineName;
        return new EnrollmentTarget(url, name, credentialsPath, hasKey);
    }

    private static string CredentialsPath(RawMcpal? mcpal, string directory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(mcpal?.CredentialsFile) ? BridgeCredentials.DefaultFileName : mcpal.CredentialsFile, directory);
```

3. `Parse`: `return Build(ParseRaw(json), null, environment, requireMcpal, storedApiKey: null);`
4. `Build(...)` gets a fifth parameter `string? storedApiKey` and passes it: `var mcpal = ParseMcpal(raw.Mcpal, environment, requireMcpal, storedApiKey);`
5. Replace the first lines and the key check of `ParseMcpal`:

```csharp
    private static McpalConfig ParseMcpal(RawMcpal? raw, IReadOnlyDictionary<string, string?> environment, bool required, string? storedApiKey)
    {
        // Keys from the environment and the credentials file are literal: only the key written in mcpal.json may refer to ${VARIABLES}.
        var apiKey = environment.GetValueOrDefault(ApiKeyVariable);
        var keyIsLiteral = !string.IsNullOrWhiteSpace(apiKey);
        if (!keyIsLiteral)
        {
            apiKey = raw?.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(storedApiKey))
            {
                apiKey = storedApiKey;
                keyIsLiteral = true;
            }
        }
```

   and in the `required` block: `apiKey = apiKey is null || keyIsLiteral ? apiKey : EnvironmentExpander.Expand(...)` and the message `$"Config section 'mcpal' needs 'apiKey' (or set {ApiKeyVariable}, or enroll this bridge with {BridgeEnroller.CodeVariable}; see the Setup page of the portal)."` — `BridgeEnroller` is created in Task 5, so for this task write the literal text `MCPAL_ENROLL` instead and switch to the constant in Task 5.
6. Add to `RawMcpal`:

```csharp
        /// <summary>File that holds the key the bridge got by enrolling; relative to the config. Default: <c>credentials.json</c> next to it.</summary>
        public string? CredentialsFile { get; set; }
```

7. In `Config/BridgeConfig.cs` add:

```csharp
/// <summary>Where <c>enroll</c> sends the code and where it keeps the key.</summary>
internal sealed record EnrollmentTarget(string? Url, string BridgeName, string CredentialsPath, bool HasKey);
```

- [ ] **Step 6: Run the bridge tests**

Run: `cd /home/jabba/MCPal && dotnet test bridge/MCPal.Bridge.Tests 2>&1 | tail -8`
Expected: all PASS (the existing loader tests keep passing: `Parse` and no-file paths are unchanged).

- [ ] **Step 7: Commit**

```bash
cd /home/jabba/MCPal && git add bridge && git commit -m "feat: bridge credentials file and key precedence" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 5: Enroller, `enroll` verb and auto-enroll on `run`

**Files:**
- Create: `bridge/MCPal.Bridge/Enrollment/BridgeEnroller.cs`, `bridge/MCPal.Bridge/Enrollment/EnrollCommand.cs`
- Modify: `bridge/MCPal.Bridge/BridgeCommandLine.cs`, `bridge/MCPal.Bridge/Program.cs`, `bridge/MCPal.Bridge/Config/BridgeConfigLoader.cs` (message constant)
- Test: `bridge/MCPal.Bridge.Tests/Enrollment/BridgeEnrollerTests.cs`, `bridge/MCPal.Bridge.Tests/Enrollment/EnrollCommandTests.cs`, `bridge/MCPal.Bridge.Tests/BridgeCommandLineTests.cs`

**Interfaces:**
- Consumes: `BridgeCredentials.Write`, `BridgeConfigLoader.LoadEnrollmentTarget`, `EnrollmentTarget` (Task 4); server `POST /api/bridge/enroll` (Task 3).
- Produces:
  - `BridgeEnroller.CodeVariable` (`"MCPAL_ENROLL"`), `BridgeEnroller.InvalidCodeMessage`, `Task<string> BridgeEnroller.EnrollAsync(HttpClient http, string serverUrl, string code, string bridgeName, string credentialsPath, CancellationToken)` returning the key; throws `EnrollmentException`.
  - `EnrollCommand.RunAsync(string configPath, string? url, string? code, IReadOnlyDictionary<string,string?> environment, HttpClient http, TextWriter output, TextWriter error, CancellationToken)` returns exit code (0 ok, 1 enrollment failed, 2 usage/config).
  - `EnrollCommand.EnrollWhenNeededAsync(string configPath, IReadOnlyDictionary<string,string?> environment, HttpClient http, TextWriter output, TextWriter error, CancellationToken)` returns 0 when nothing to do or enrolled.
  - `BridgeCommandLine.Enroll` (`"enroll"`), a known verb.

- [ ] **Step 1: Write the failing enroller tests**

Create `bridge/MCPal.Bridge.Tests/Enrollment/BridgeEnrollerTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class BridgeEnrollerTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static string NewFile() => Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"), "credentials.json");

    private static void Cleanup(string file)
    {
        var directory = Path.GetDirectoryName(file);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Test]
    public async Task EnrollAsync_ValidCode_PostsCodeAndNameSavesKeyAndReturnsIt()
    {
        var file = NewFile();
        try
        {
            var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com","apiKey":"mcpal_aaaaaaaa_new"}"""));
            using var http = new HttpClient(handler);

            var key = await BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com/", "mcpale_code", "hq-01", file, Ct);

            key.Should().Be("mcpal_aaaaaaaa_new");
            BridgeCredentials.TryRead(file).Should().Be("mcpal_aaaaaaaa_new");
            handler.Requests.Should().ContainSingle();
            handler.Requests[0].Uri.Should().Be(new Uri("https://mcpal.example.com/api/bridge/enroll"));
            using var body = JsonDocument.Parse(handler.Requests[0].Body);
            body.RootElement.GetProperty("code").GetString().Should().Be("mcpale_code");
            body.RootElement.GetProperty("bridgeName").GetString().Should().Be("hq-01");
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task EnrollAsync_InvalidCode_ThrowsClearMessageAndWritesNoFile()
    {
        var file = NewFile();
        try
        {
            using var http = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));

            var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", file, Ct);

            (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage(BridgeEnroller.InvalidCodeMessage);
            File.Exists(file).Should().BeFalse();
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task EnrollAsync_TooManyRequests_SaysToWait()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*wait*");
    }

    [Test]
    public async Task EnrollAsync_ServerError_MentionsStatusAndUrl()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*500*https://mcpal.example.com/api/bridge/enroll*");
    }

    [Test]
    public async Task EnrollAsync_NetworkError_MentionsTheUrl()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*https://mcpal.example.com/api/bridge/enroll*connection refused*");
    }

    [Test]
    public async Task EnrollAsync_AnswerWithoutKey_Throws()
    {
        using var http = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com"}""")));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*no key*");
    }
}
```

- [ ] **Step 2: Write the failing command tests**

Create `bridge/MCPal.Bridge.Tests/Enrollment/EnrollCommandTests.cs`:

```csharp
using System.Net;
using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class EnrollCommandTests
{
    private const string DockerLikeConfig = """{ "mcpal": { "url": "${MCPAL_URL}", "bridgeName": "hq-01" } }""";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static (string Directory, string Config) NewConfig(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "mcpal.json");
        File.WriteAllText(config, json);
        return (directory, config);
    }

    private static BridgeEnrollerTests.StubHandler Accepting() =>
        new(_ => BridgeEnrollerTests.Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com","apiKey":"mcpal_aaaaaaaa_new"}"""));

    private static Dictionary<string, string?> Env(string? code = "mcpale_code") => new() { ["MCPAL_URL"] = "https://mcpal.example.com", ["MCPAL_ENROLL"] = code };

    [Test]
    public async Task RunAsync_CodeFromEnvironment_EnrollsAndPrintsWhereTheKeyIs()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, url: null, code: null, Env(), http, output, error, Ct);

            exit.Should().Be(0);
            BridgeCredentials.TryRead(Path.Combine(directory, "credentials.json")).Should().Be("mcpal_aaaaaaaa_new");
            output.ToString().Should().Contain("hq-01").And.Contain("credentials.json");
            error.ToString().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_UrlAndCodeArguments_BeatEnvironmentAndConfig()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.RunAsync(config, "https://other.example.com", "mcpale_given", Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests[0].Uri.Should().Be(new Uri("https://other.example.com/api/bridge/enroll"));
            handler.Requests[0].Body.Should().Contain("mcpale_given");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_NoCode_ExitsWithUsageError()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(Accepting());
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, Env(code: null), http, new StringWriter(), error, Ct);

            exit.Should().Be(2);
            error.ToString().Should().Contain("--code").And.Contain("MCPAL_ENROLL");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_NoUrlAnywhere_ExitsWithUsageError()
    {
        var (directory, config) = NewConfig("{}");
        try
        {
            using var http = new HttpClient(Accepting());
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, new Dictionary<string, string?> { ["MCPAL_ENROLL"] = "mcpale_code" }, http, new StringWriter(), error, Ct);

            exit.Should().Be(2);
            error.ToString().Should().Contain("--url");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_InvalidCode_PrintsTheMessageAndExitsWithOne()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(new BridgeEnrollerTests.StubHandler(_ => BridgeEnrollerTests.Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, Env(), http, new StringWriter(), error, Ct);

            exit.Should().Be(1);
            error.ToString().Should().Contain(BridgeEnroller.InvalidCodeMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoCodeVariable_DoesNothing()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(code: null), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_KeyAlreadyStored_DoesNotCallTheServerAgain()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
            BridgeCredentials.TryRead(Path.Combine(directory, "credentials.json")).Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_KeyInEnvironment_DoesNotCallTheServer()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);
            var environment = Env();
            environment["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env";

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, environment, http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoKeyAndCode_EnrollsAndSavesTheKey()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(Accepting());

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            BridgeConfigLoader.Load(config, Env(), requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_new");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoKeyAndSpentCode_FailsWithTheInvalidCodeMessage()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(new BridgeEnrollerTests.StubHandler(_ => BridgeEnrollerTests.Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));
            var error = new StringWriter();

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), error, Ct);

            exit.Should().Be(1);
            error.ToString().Should().Contain(BridgeEnroller.InvalidCodeMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

In `BridgeCommandLineTests.cs` add:

```csharp
    [Test]
    public void Parse_EnrollVerb_IsKnownAndKeepsItsOptions()
    {
        var parsed = BridgeCommandLine.Parse(["enroll", "--url", "https://mcpal.example.com", "--code", "mcpale_x"]);

        parsed.IsKnownVerb.Should().BeTrue();
        parsed.Verb.Should().Be("enroll");
        parsed.HostArgs.Should().Equal("--url", "https://mcpal.example.com", "--code", "mcpale_x");
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `cd /home/jabba/MCPal && dotnet test bridge/MCPal.Bridge.Tests --filter "FullyQualifiedName~Enroll" 2>&1 | tail -15`
Expected: build FAIL (`BridgeEnroller`, `EnrollCommand`, `EnrollmentException` missing).

- [ ] **Step 4: Implement `BridgeEnroller`**

Create `bridge/MCPal.Bridge/Enrollment/BridgeEnroller.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace MCPal.Bridge.Enrollment;

/// <summary>Enrolling failed in a way the operator can act on; the message says how.</summary>
internal sealed class EnrollmentException(string message) : Exception(message);

/// <summary>Trades an enrollment code (made on the portal's Setup page) for a bridge key and saves the key in the credentials file.</summary>
internal static class BridgeEnroller
{
    public const string CodeVariable = "MCPAL_ENROLL";

    public const string InvalidCodeMessage = "The enrollment code is invalid, expired or already used. Create a new one on the Setup page of the portal.";

    /// <returns>The new bridge key (also written to <paramref name="credentialsPath"/>).</returns>
    public static async Task<string> EnrollAsync(HttpClient http, string serverUrl, string code, string bridgeName, string credentialsPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPath);

        var endpoint = new Uri(serverUrl.TrimEnd('/') + "/api/bridge/enroll");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(endpoint, new { code, bridgeName }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new EnrollmentException($"Cannot reach {endpoint}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EnrollmentException($"No answer from {endpoint} (timeout).");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new EnrollmentException(InvalidCodeMessage);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new EnrollmentException("Too many enrollment attempts from this address. Wait a minute and try again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EnrollmentException($"Enrollment at {endpoint} failed: HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken);
            if (result?.ApiKey is not { Length: > 0 } key)
            {
                throw new EnrollmentException($"The answer of {endpoint} holds no key.");
            }

            BridgeCredentials.Write(credentialsPath, key);
            return key;
        }
    }

    private sealed record EnrollResponse(string? Url, string? ApiKey);
}
```

- [ ] **Step 5: Implement `EnrollCommand`**

Create `bridge/MCPal.Bridge/Enrollment/EnrollCommand.cs`:

```csharp
using MCPal.Bridge.Config;

namespace MCPal.Bridge.Enrollment;

/// <summary>The <c>enroll</c> verb and the automatic enrollment of <c>run</c>.</summary>
internal static class EnrollCommand
{
    /// <summary>Enrolls with the given code (argument, else <c>MCPAL_ENROLL</c>). Exit code 0 enrolled, 1 enrollment failed, 2 missing input.</summary>
    public static async Task<int> RunAsync(
        string configPath,
        string? url,
        string? code,
        IReadOnlyDictionary<string, string?> environment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var enrollmentCode = FirstNonBlank(code, environment.GetValueOrDefault(BridgeEnroller.CodeVariable));
        if (enrollmentCode is null)
        {
            error.WriteLine($"Give the enrollment code with --code or {BridgeEnroller.CodeVariable}. Create one on the Setup page of the portal.");
            return 2;
        }

        var target = BridgeConfigLoader.LoadEnrollmentTarget(configPath, environment, url);
        if (target.Url is null)
        {
            error.WriteLine("Give the MCPal server URL with --url, MCPAL_URL (when mcpal.json refers to it) or 'mcpal.url' in mcpal.json.");
            return 2;
        }

        try
        {
            await BridgeEnroller.EnrollAsync(http, target.Url, enrollmentCode, target.BridgeName, target.CredentialsPath, cancellationToken);
        }
        catch (EnrollmentException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }

        output.WriteLine($"Enrolled bridge '{target.BridgeName}' at {target.Url}. The key is saved in {target.CredentialsPath}.");
        return 0;
    }

    /// <summary>
    /// For <c>run</c>: enrolls only when <c>MCPAL_ENROLL</c> is set and the bridge has no key yet. A restarted container keeps its key and
    /// ignores the (spent) code. Returns 0 when there was nothing to do or enrolling worked.
    /// </summary>
    public static async Task<int> EnrollWhenNeededAsync(
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(environment.GetValueOrDefault(BridgeEnroller.CodeVariable)))
        {
            return 0;
        }

        if (BridgeConfigLoader.LoadEnrollmentTarget(configPath, environment, urlOverride: null).HasKey)
        {
            return 0;
        }

        return await RunAsync(configPath, url: null, code: null, environment, http, output, error, cancellationToken);
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
```

- [ ] **Step 6: Verb and Program**

`BridgeCommandLine.cs`: add `public const string Enroll = "enroll";`, change `IsKnownVerb => Verb is Run or Check or Status or Enroll;` and extend the summary: `"enroll" trades an enrollment code for a bridge key and saves it.`

`Program.cs`:
- usage line: `"Unknown verb '{commandLine.Verb}'. Usage: MCPal.Bridge [run|check|status|enroll] [--config <path>]"`.
- add `using MCPal.Bridge.Enrollment;`.
- inside the `try`, directly after the `Status` block and before `// Fail fast …`:

```csharp
    if (commandLine.Verb is BridgeCommandLine.Enroll or BridgeCommandLine.Run)
    {
        var configPath = BridgeModule.ResolveConfigPath(builder.Configuration);
        var environment = BridgeConfigLoader.CurrentEnvironment();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var exit = commandLine.Verb == BridgeCommandLine.Enroll
            ? await EnrollCommand.RunAsync(configPath, builder.Configuration["url"], builder.Configuration["code"], environment, http, Console.Out, Console.Error, CancellationToken.None)
            : await EnrollCommand.EnrollWhenNeededAsync(configPath, environment, http, Console.Out, Console.Error, CancellationToken.None);
        if (exit != 0 || commandLine.Verb == BridgeCommandLine.Enroll)
        {
            return exit;
        }
    }
```

In `BridgeConfigLoader.cs` replace the literal `MCPAL_ENROLL` in the missing-key message by `{BridgeEnroller.CodeVariable}`.

- [ ] **Step 7: Run the bridge tests**

Run: `cd /home/jabba/MCPal && dotnet build MCPal.slnx 2>&1 | tail -3 && dotnet test bridge/MCPal.Bridge.Tests 2>&1 | tail -8`
Expected: 0 warnings, all PASS.

- [ ] **Step 8: Smoke test the verb by hand**

Run: `cd /home/jabba/MCPal && dotnet run --project bridge/MCPal.Bridge -- enroll --config /nonexistent.json --url https://x.invalid --code mcpale_x 2>&1 | tail -3`
Expected: `Configuration error: Cannot read config file '/nonexistent.json' …` and exit code 2 (the verb is wired and failures are readable).

- [ ] **Step 9: Commit**

```bash
cd /home/jabba/MCPal && git add bridge && git commit -m "feat: bridge enroll verb and automatic enrollment on run" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 6: End-to-end test

**Files:**
- Create: `tests/MCPal.E2E.Tests/EnrollmentE2ETests.cs`

**Interfaces:**
- Consumes: `E2EStack.CreateAsync`, `SeedCompanyAsync`, `StartBridgeAsync(SeededCompany, serverName, bridgeName, ct)`, `ConnectClientAsync`, `WaitForToolAsync` (existing); `BridgeEnrollmentService`, `BridgeEnroller`, `BridgeCredentials` (Tasks 2, 4, 5).

- [ ] **Step 1: Write the test**

```csharp
using MCPal.Bridge.Enrollment;
using MCPal.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.E2E.Tests;

/// <summary>A bridge that only knows an enrollment code gets its key, connects and serves a tool call.</summary>
[TestFixture]
internal sealed class EnrollmentE2ETests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Enroll_ThenConnect_ServesToolsWithTheEnrolledKey()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        string code;
        await using (var scope = stack.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<BridgeEnrollmentService>().CreateAsync(acme.CompanyId, acme.OwnerUserId, Ct);
            code = created?.Code ?? throw new InvalidOperationException("No code created.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcpal-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var credentials = Path.Combine(directory, "credentials.json");
            using var http = stack.CreateClient();

            var key = await BridgeEnroller.EnrollAsync(http, "http://localhost", code, "hq-enrolled", credentials, Ct);

            BridgeCredentials.TryRead(credentials).Should().Be(key);
            using var bridge = await stack.StartBridgeAsync(acme with { BridgeKey = key }, "test", "hq-enrolled", Ct);
            await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
            await E2EStack.WaitForToolAsync(client, "test__echo", Ct);
            var echo = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: Ct);
            echo.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text.Should().Be("echo: hi");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
```

If `StartBridgeAsync` has a different parameter order, read its signature in `tests/MCPal.E2E.Tests/Infrastructure/E2EStack.cs` (it is called as `stack.StartBridgeAsync(acme, "test", "hq-01", Ct)` in `EndToEndTests`) and adapt.

- [ ] **Step 2: Run it**

Run: `cd /home/jabba/MCPal && dotnet test tests/MCPal.E2E.Tests --filter "FullyQualifiedName~EnrollmentE2ETests" 2>&1 | tail -10`
Expected: PASS (needs Docker; the E2E project builds the test MCP server first).

- [ ] **Step 3: Commit**

```bash
cd /home/jabba/MCPal && git add tests && git commit -m "test: enrollment end to end" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 7: Installers and Docker files

**Files:**
- Modify: `bridge/packaging/linux/install.sh`, `bridge/packaging/linux/test-install.sh`
- Modify: `bridge/packaging/windows/install.ps1`
- Modify: `bridge/packaging/docker/mcpal.docker.json`, `bridge/packaging/docker/compose.yml`, `bridge/packaging/docker/README.md`

**Interfaces:**
- Consumes: `mcpal-bridge enroll --config <path> --code <code>` (Task 5; reads the URL from `mcpal.json`, writes `credentials.json` next to it).
- Produces: `install.sh --enroll <code>`, `install.ps1 -Enroll <code>`; Docker image reads the key from `/data/credentials.json`.

- [ ] **Step 1: Extend the Linux installer test first**

In `bridge/packaging/linux/test-install.sh`, insert this block before the line `# Unpacked files missing: refuse.`:

```bash
# Enrollment: the stub bridge writes credentials.json next to --config like the real `enroll` verb.
cat > "$archive/mcpal-bridge" <<'STUB'
#!/bin/sh
if [ "$1" = enroll ]; then
  while [ $# -gt 0 ]; do
    case "$1" in --config) cfg="$2" ;; --code) code="$2" ;; esac
    shift
  done
  if [ "$code" = mcpale_bad ]; then echo "The enrollment code is invalid" >&2; exit 1; fi
  printf '{"apiKey":"mcpal_enrolled_%s"}\n' "$code" > "$(dirname "$cfg")/credentials.json"
  chmod 600 "$(dirname "$cfg")/credentials.json"
  exit 0
fi
echo stub bridge
STUB
chmod 755 "$archive/mcpal-bridge"
saved_root="$root"; root="$work/root-enroll"; mkdir -p "$root"
run_install --enroll mcpale_good
[[ -f "$root/etc/mcpal/credentials.json" ]] || fail "enroll did not create credentials.json"
[[ "$(mode "$root/etc/mcpal/credentials.json")" == "640" ]] || fail "credentials mode is $(mode "$root/etc/mcpal/credentials.json"), want 640"
grep -q 'mcpal_enrolled_mcpale_good' "$root/etc/mcpal/credentials.json" || fail "credentials.json does not hold the enrolled key"
grep -q '^MCPAL_API_KEY=$' "$root/etc/mcpal/bridge.env" || fail "env file should hold an empty key when enrolling"
if MCPAL_INSTALL_ROOT="$root" MCPAL_SKIP_SERVICE=1 "$archive/install.sh" --enroll mcpale_bad > "$work/out.txt" 2>&1; then fail "install.sh accepted a bad code"; fi
grep -q 'invalid' "$work/out.txt" || fail "no clear message for a bad code"
if MCPAL_INSTALL_ROOT="$root" MCPAL_SKIP_SERVICE=1 "$archive/install.sh" --enroll mcpale_good --api-key mcpal_x > "$work/out.txt" 2>&1; then fail "install.sh accepted --enroll together with --api-key"; fi
grep -q 'not both' "$work/out.txt" || fail "no clear message for --enroll with --api-key"
root="$saved_root"

```

Run: `cd /home/jabba/MCPal && bash bridge/packaging/linux/test-install.sh 2>&1 | tail -5`
Expected: FAIL with `Unknown option: --enroll`.

- [ ] **Step 2: Implement `--enroll` in `install.sh`**

- Header comment: add `#   sudo ./install.sh [--enroll <code> | --api-key mcpal_...]` and the line `# --enroll trades a one-time code from the portal's Setup page for a bridge key (saved in /etc/mcpal/credentials.json, mode 640).`; change the help `sed -n '2,10p'` to `2,12p`.
- Variables: `enroll_code="${MCPAL_ENROLL:-}"` after `api_key=`.
- Option parsing: `--enroll) enroll_code="${2:?--enroll needs a value}"; shift 2 ;;`
- After the option loop:

```bash
if [[ -n "$enroll_code" && -n "$api_key" ]]; then
  echo "Give --enroll or --api-key, not both." >&2
  exit 2
fi
```

- Insert before the `if [[ "$skip_service" != "1" ]]; then` block that does the `chown` (the one after the env file section):

```bash
if [[ -n "$enroll_code" ]]; then
  echo "Enrolling this bridge ..."
  if ! "$bin_dir/mcpal-bridge" enroll --config "$config" --code "$enroll_code"; then
    echo "Enrollment failed, so the bridge has no key. Create a new code on the Setup page and run the script again." >&2
    exit 1
  fi
  # Written as root with mode 600; the service user reads it through the group.
  chmod 640 "$config_dir/credentials.json"
fi
```

- At the end replace the last line with:

```bash
systemctl restart mcpal-bridge.service
echo "MCPal bridge started. Follow it with: journalctl -u mcpal-bridge -f"
echo "Your local MCP servers are in $servers; restart the service after changing it. The portal's Setup page shows the bridge as online."
```

Run: `cd /home/jabba/MCPal && bash bridge/packaging/linux/test-install.sh 2>&1 | tail -5`
Expected: `install.sh tests passed`.

Note: with `--enroll` the existing env-file branch still writes `MCPAL_API_KEY=` (empty) and prints "Put the API key … into $env_file". Make that hint conditional: change `if [[ -z "$api_key" ]]; then` to `if [[ -z "$api_key" && -z "$enroll_code" ]]; then`.

- [ ] **Step 3: Implement `-Enroll` in `install.ps1`**

- Add to the help: `.EXAMPLE` `.\install.ps1 -Enroll mcpale_xxxxxxxx...` and a DESCRIPTION sentence: `-Enroll trades a one-time code from the portal's Setup page for a bridge key, stored in <DataDir>\credentials.json (protected like the rest of <DataDir>).`
- Parameter: `[string]$Enroll = $env:MCPAL_ENROLL,` after `$ApiKey`.
- After the parameter check block (`$here = …` area) add:

```powershell
if ($Enroll -and $ApiKey) {
    throw 'Give -Enroll or -ApiKey, not both.'
}
```

- After the `icacls` block (so new files inherit the restricted ACL) and before `$binaryPath = …`:

```powershell
if ($Enroll) {
    Write-Host 'Enrolling this bridge ...'
    & $exe enroll --config $config --code $Enroll
    if ($LASTEXITCODE -ne 0) {
        throw 'Enrollment failed, so the bridge has no key. Create a new code on the Setup page and run the script again.'
    }
}
```

- Replace the `else { Write-Warning 'No API key given …' }` with:

```powershell
} elseif (-not $Enroll -and -not (Test-Path (Join-Path $DataDir 'credentials.json'))) {
    Write-Warning 'No key given. Run the script again with -Enroll <code> (from the Setup page) or -ApiKey mcpal_..., or set MCPAL_API_KEY for the service yourself.'
}
```

  (the `if ($ApiKey) { … }` branch stays as it is; make sure the braces form `if ($ApiKey) { … } elseif (…) { … }`.)
- Final message: add `Write-Host "Your local MCP servers are in $servers; restart the service after changing it. The portal's Setup page shows the bridge as online."`

There is no PowerShell test harness here; verify by reading the diff and, if `pwsh` exists, `pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('bridge/packaging/windows/install.ps1',[ref]$null,[ref]$e); $e"` prints nothing.

- [ ] **Step 4: Docker files**

- `mcpal.docker.json`:

```json
{
  "mcpal": {
    "url": "${MCPAL_URL}",
    "mcpServersFile": "/config/mcp.json",
    "credentialsFile": "/data/credentials.json"
  },
  "statusFile": "/data/status.json",
  "jwksFile": "/data/jwks.json"
}
```

- `compose.yml`: change the comment (`The portal's Setup page fills in the values for you.` stays) and the environment block to

```yaml
    environment:
      MCPAL_URL: ${MCPAL_URL:?set MCPAL_URL to the URL of your MCPal server}
      # One of the two: MCPAL_ENROLL is the one-time code from the Setup page (the bridge fetches and keeps its own key in the
      # data volume); MCPAL_API_KEY is a bridge key you created yourself.
      MCPAL_ENROLL: ${MCPAL_ENROLL:-}
      MCPAL_API_KEY: ${MCPAL_API_KEY:-}
```

  and the header example line to `MCPAL_URL=https://mcpal.example.com MCPAL_ENROLL=mcpale_... docker compose up -d`.
- `README.md` (docker folder): the `docker run` example uses `-e MCPAL_ENROLL=mcpale_...` (with a sentence: the code works once and lasts 15 minutes; the bridge keeps its key in `/data/credentials.json`, so keep the volume; a restart ignores the code), the contract table row `Bridge key | MCPAL_ENROLL (one-time code from the Setup page) or MCPAL_API_KEY (a bridge key); one of them is required`, and a row `Saved key | /data/credentials.json (mode 600), written by enrollment`.

- [ ] **Step 5: Run the image check**

Run: `cd /home/jabba/MCPal && bridge/packaging/docker/test-image.sh 2>&1 | tail -8`
Expected: image builds and `check` passes (needs Docker; skip with a note in the PR if the build is not possible offline).

- [ ] **Step 6: Commit**

```bash
cd /home/jabba/MCPal && git add bridge && git commit -m "feat: install scripts and Docker files accept an enrollment code" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 8: Setup page

**Files:**
- Modify: `server/portal/src/api/types.ts`, `server/portal/src/api/client.ts`, `server/portal/src/i18n/en.ts`, `server/portal/src/pages/SetupPage.tsx`
- Test: `server/portal/src/pages/SetupPage.test.tsx`

**Interfaces:**
- Consumes: `POST /api/portal/setup/enrollments` → `{ code, expiresAt }`, 409 → `{ errors: [...] }` (Task 3).
- Produces: `api.createEnrollment()`, type `Enrollment`; commands for Docker (`-e MCPAL_ENROLL=<code>`), Linux (`sudo ./install.sh --enroll <code>`), Windows (`.\install.ps1 -Enroll <code>`); placeholder `<enrollment-code>`; the manual key flow behind a toggle button `Use a bridge key instead (advanced)`.

- [ ] **Step 1: Update and add tests**

In `SetupPage.test.tsx`:

1. Add `const enrollmentCode = 'mcpale_' + 'B'.repeat(24);` next to `bridgeKey`. Extend `backend` options with `enrollment?: { code: string; expiresAt: string }` and `enrollStatus?: number`, and add before `return undefined;`:

```ts
    if (call.url === '/api/portal/setup/enrollments' && call.method === 'POST') {
      if (options.enrollStatus === 409) return { status: 409, body: { errors: ['Too many unused enrollment codes. Use one or wait until it expires, then try again.'] } };
      return { status: 201, body: options.enrollment ?? { code: enrollmentCode, expiresAt: new Date(Date.now() + 15 * 60_000).toISOString() } };
    }
```

   (change the `backend` signature to `options: { setup?: Setup; connections?: Connection[]; enrollment?: { code: string; expiresAt: string }; enrollStatus?: number } = {}`).
2. Replace the five tests that mention the old default commands/placeholders and add new ones:

```tsx
  it('generates an enrollment code and puts it into the install command', async () => {
    const calls = mockFetch(backend());
    renderPage();
    await chooseLinux();
    expect(await screen.findByText('sudo ./install.sh --enroll <enrollment-code>')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Generate command' }));

    expect(await screen.findByText(`sudo ./install.sh --enroll ${enrollmentCode}`)).toBeInTheDocument();
    expect(screen.getByText(/Code valid for \d+:\d\d/)).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST' && c.url === '/api/portal/setup/enrollments')).toBe(true);
    expect(calls.some((c) => c.url === '/api/portal/keys')).toBe(false);
  });

  it('puts the enrollment code into the docker run command and needs no key', async () => {
    mockFetch(backend());
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Generate command' }));

    expect(await screen.findByText(new RegExp(`-e MCPAL_ENROLL=${enrollmentCode} `))).toBeInTheDocument();
    expect(screen.queryByText(/MCPAL_API_KEY/)).not.toBeInTheDocument();
  });

  it('uses the Windows enroll option for the Windows platform', async () => {
    mockFetch(backend());
    renderPage();
    await userEvent.click(await screen.findByRole('radio', { name: 'Windows x64' }));

    expect(screen.getByText('.\\install.ps1 -Enroll <enrollment-code>')).toBeInTheDocument();
    expect(screen.getByText('Expand-Archive mcpal-bridge-1.2.3-win-x64.zip .')).toBeInTheDocument();
  });

  it('removes an expired code from the command and offers a new one', async () => {
    mockFetch(backend({ enrollment: { code: enrollmentCode, expiresAt: '2020-01-01T00:00:00Z' } }));
    renderPage();
    await chooseLinux();
    await userEvent.click(await screen.findByRole('button', { name: 'Generate command' }));

    expect(await screen.findByText('This code has expired. Generate a new one.')).toBeInTheDocument();
    expect(screen.getByText('sudo ./install.sh --enroll <enrollment-code>')).toBeInTheDocument();
    expect(screen.queryByText(new RegExp(enrollmentCode))).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Generate a new code' })).toBeInTheDocument();
  });

  it('shows the server message when too many codes are open', async () => {
    mockFetch(backend({ enrollStatus: 409 }));
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Generate command' }));

    expect(await screen.findByText(/Too many unused enrollment codes/)).toBeInTheDocument();
  });

  it('still offers a bridge key as an advanced option and then uses it in the commands', async () => {
    const calls = mockFetch(backend());
    renderPage();
    await chooseLinux();
    expect(screen.queryByRole('button', { name: 'Create bridge key' })).not.toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Use a bridge key instead (advanced)' }));
    await userEvent.click(screen.getByRole('button', { name: 'Create bridge key' }));

    expect(await screen.findByTestId('created-key')).toHaveTextContent(bridgeKey);
    expect(screen.getByText(`sudo ./install.sh --api-key ${bridgeKey}`)).toBeInTheDocument();
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ name: 'Main bridge', purpose: 'bridge' });
  });

  it('puts a created key into the docker run command', async () => {
    mockFetch(backend());
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Use a bridge key instead (advanced)' }));
    await userEvent.click(screen.getByRole('button', { name: 'Create bridge key' }));

    expect(await screen.findByText(new RegExp(`-e MCPAL_API_KEY=${bridgeKey} `))).toBeInTheDocument();
    expect(screen.queryByText(/MCPAL_ENROLL/)).not.toBeInTheDocument();
  });
```

   and change the existing assertion in `'runs the bridge as a container by default'` to `-e MCPAL_ENROLL=<enrollment-code> `. Delete the old tests `'creates a bridge key, shows it once and puts it into the install command'`, `'uses the Windows commands for the Windows platform'` and `'puts the created key into the docker run command'` (replaced above).

- [ ] **Step 2: Run to verify they fail**

Run: `cd /home/jabba/MCPal/server/portal && npx vitest run src/pages/SetupPage.test.tsx 2>&1 | tail -20`
Expected: FAIL (no "Generate command" button yet).

- [ ] **Step 3: Types, client, strings**

`api/types.ts`, next to `CreatedApiKey`:

```ts
/** A one-time code a bridge trades for its own key. Shown once; the server keeps only a hash. */
export interface Enrollment {
  code: string;
  expiresAt: string;
}
```

`api/client.ts` (import `Enrollment` with the other types) next to `setup`:

```ts
  createEnrollment: () => request<Enrollment>('POST', '/api/portal/setup/enrollments'),
```

`i18n/en.ts`: add after `'setup.key.placeholder'`:

```ts
  'setup.enroll.title': 'Generate the install command',
  'setup.enroll.body': 'The bridge signs in to MCPal with a one-time code, so you copy no key. The code works once and expires after a few minutes. Generate it right before you run the command in step 4.',
  'setup.enroll.generate': 'Generate command',
  'setup.enroll.again': 'Generate a new code',
  'setup.enroll.valid': 'Code valid for {time}',
  'setup.enroll.expired': 'This code has expired. Generate a new one.',
  'setup.enroll.placeholder': '<enrollment-code>',
  'setup.key.toggle': 'Use a bridge key instead (advanced)',
```

and change `'setup.key.title'` to `'Create a bridge key (advanced)'` and `'setup.key.body'` to `'Use this when you want one key for several bridges. The bridge signs in to MCPal with it. It is shown once, so create it now and use it in step 4.'`.

- [ ] **Step 4: Implement the page**

Replace the credential handling in `SetupPage.tsx`:

1. Imports: `import { useEffect, useState, type FormEvent } from 'react';` and `import type { CreatedApiKey, Enrollment, Setup } from '../api/types';`.
2. Replace `dockerCommands` and `installCommands` signatures and bodies:

```tsx
/** What the bridge signs in with: a one-time enrollment code (default) or a bridge key the owner created. */
type Credential = { kind: 'enroll' | 'key'; value: string };

/** The container needs no mcpal.json: the server URL and the credential are environment variables. */
function dockerCommands(setup: Setup, credential: Credential): string[] {
  const variable = credential.kind === 'enroll' ? 'MCPAL_ENROLL' : 'MCPAL_API_KEY';
  return [
    `docker run -d --name mcpal-bridge --hostname mcpal-bridge --restart unless-stopped -e MCPAL_URL=${setup.mcpalUrl} -e ${variable}=${credential.value} -v "$PWD/mcp.json:/config/mcp.json:ro" -v mcpal-bridge-data:/data ${setup.imageReference}`,
    'docker logs -f mcpal-bridge',
  ];
}

/** The install commands of the release archive. The credential is part of the command, not of mcpal.json. */
function installCommands(setup: Setup, platform: Platform, credential: Credential): string[] {
  const { fileName, folder } = archiveOf(setup, platform);
  if (platform === 'win-x64') {
    return [
      `Expand-Archive ${fileName} .`,
      `cd ${folder}`,
      'New-Item -Force -ItemType Directory $env:ProgramData\\MCPal | Out-Null',
      'Copy-Item ..\\mcpal.json, ..\\mcp.json $env:ProgramData\\MCPal\\',
      `.\\install.ps1 ${credential.kind === 'enroll' ? '-Enroll' : '-ApiKey'} ${credential.value}`,
    ];
  }
  return [
    `tar xzf ${fileName}`,
    `cd ${folder}`,
    'sudo install -D -m 640 ../mcpal.json /etc/mcpal/mcpal.json',
    'sudo install -m 640 ../mcp.json /etc/mcpal/mcp.json',
    `sudo ./install.sh ${credential.kind === 'enroll' ? '--enroll' : '--api-key'} ${credential.value}`,
  ];
}

/** Seconds from now to the given time, never below zero. */
function secondsLeft(expiresAt: string, now: number): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - now) / 1000));
}

function clock(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}
```

3. In `SetupPage()` replace the key-related state/hooks with:

```tsx
  const [method, setMethod] = useState<Method>('docker');
  const [keyName, setKeyName] = useState(defaultKeyName);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [showKey, setShowKey] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const queryClient = useQueryClient();
  const setup = useQuery({ queryKey: ['setup'], queryFn: api.setup });
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections, refetchInterval: 3000 });
  const createEnrollment = useMutation({
    mutationFn: api.createEnrollment,
    onSuccess: (result) => {
      setEnrollment(result);
      setNow(Date.now());
    },
  });
  const createKey = useMutation({
    mutationFn: () => api.createKey(keyName, { purpose: 'bridge' }),
    onSuccess: async (key) => {
      setCreated(key);
      await queryClient.invalidateQueries({ queryKey: ['keys'] });
    },
  });

  useEffect(() => {
    if (enrollment === null) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [enrollment]);

  function submitKey(event: FormEvent) {
    event.preventDefault();
    createKey.mutate();
  }

  const remaining = enrollment === null ? null : secondsLeft(enrollment.expiresAt, now);
  const expired = remaining === 0;
  // A created key wins (the owner chose the advanced path); an expired code never stays in a command someone might paste.
  const credential: Credential =
    created !== null
      ? { kind: 'key', value: created.key }
      : { kind: 'enroll', value: enrollment !== null && !expired ? enrollment.code : t('setup.enroll.placeholder') };
```

   Remove the `keyOrPlaceholder` constant; the `commands` call sites use `credential`.
4. Replace the whole second `<li>` (the `setup.key.title` step) with:

```tsx
          <li>
            <h2>{t('setup.enroll.title')}</h2>
            <div className="step-body">
              <p>{t('setup.enroll.body')}</p>
              <div className="row">
                <button type="button" disabled={createEnrollment.isPending} onClick={() => createEnrollment.mutate()}>
                  {t(enrollment === null ? 'setup.enroll.generate' : 'setup.enroll.again')}
                </button>
                {remaining !== null && (
                  <span className={expired ? 'muted' : undefined} aria-live="polite">
                    {expired ? t('setup.enroll.expired') : t('setup.enroll.valid', { time: clock(remaining) })}
                  </span>
                )}
              </div>
              <ErrorText error={createEnrollment.error} />
              <button type="button" className="ghost small" onClick={() => setShowKey((shown) => !shown)}>
                {t('setup.key.toggle')}
              </button>
              {showKey && (
                <div>
                  <h3>{t('setup.key.title')}</h3>
                  <p>{t('setup.key.body')}</p>
                  {created === null ? (
                    <form className="row" onSubmit={submitKey}>
                      <label className="grow">
                        {t('setup.key.name')}
                        <input value={keyName} onChange={(event) => setKeyName(event.target.value)} required />
                      </label>
                      <button type="submit" disabled={createKey.isPending}>
                        {t('setup.key.create')}
                      </button>
                    </form>
                  ) : (
                    <section className="notice hazard" aria-live="polite">
                      <p>{t('keys.createdBody')}</p>
                      <div className="row secret">
                        <code data-testid="created-key">{created.key}</code>
                        <CopyButton value={created.key} />
                      </div>
                    </section>
                  )}
                  <ErrorText error={createKey.error} />
                </div>
              )}
            </div>
          </li>
```

5. In the install step: `(method === 'docker' ? dockerCommands(data, credential) : installCommands(data, method, credential))`.

   The `'setup.key.placeholder'` string is no longer used by the page; leave it (a later cleanup may remove it) unless `npm run typecheck`/lint flags unused keys.

- [ ] **Step 5: Run the SPA checks**

Run: `cd /home/jabba/MCPal/server/portal && npx vitest run src/pages/SetupPage.test.tsx 2>&1 | tail -15 && npm run typecheck 2>&1 | tail -5 && npm test 2>&1 | tail -6`
Expected: all PASS, typecheck clean. If a Home/Login/e2e test expects the old Setup page strings, fix those expectations (search with `grep -rn "Create bridge key\|api-key <your" server/portal/src server/portal/e2e`).

- [ ] **Step 6: Commit**

```bash
cd /home/jabba/MCPal && git add server/portal && git commit -m "feat: Setup page generates an enrollment command" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 9: Documentation

**Files:**
- Modify: `README.md`, `bridge/packaging/README.linux.txt`, `bridge/packaging/README.windows.txt`, `docs/tunnel-protocol.md`, `docs/next-steps.md`, `CHANGELOG.md`, `plan.md`, `docs/superpowers/specs/2026-10-04-easy-setup-design.md`

- [ ] **Step 1: README quickstart**

In `README.md` step 2 and 3:
- Step 2: keep the owner/sign-up text but replace the bridge key sentence with: "The bridge needs no key from you: the Setup page creates a one-time enrollment code. Create a **Bridge key** by hand under **API keys** only when you want one key for several bridges."
- Step 3: start with the enrollment flow. The Setup page shows the command with the code filled in; show the Docker command first:

```bash
docker run -d --name mcpal-bridge --hostname mcpal-bridge --restart unless-stopped \
  -e MCPAL_URL=https://mcpal.example.com \
  -e MCPAL_ENROLL=mcpale_… \
  -v "$PWD/mcp.json:/config/mcp.json:ro" \
  -v mcpal-bridge-data:/data \
  ghcr.io/jabbakadabra/mcpal-bridge:latest
```

  Add the explanation: the code works once and lasts 15 minutes (`Mcpal__EnrollmentLifetimeMinutes`), the bridge fetches its own bridge key and keeps it in `/data/credentials.json` (keep the volume; a restart ignores the code), and `MCPAL_API_KEY` with a key you created yourself still works. For the archives: `sudo ./install.sh --enroll mcpale_…` and `.\install.ps1 -Enroll mcpale_…`; `--api-key`/`-ApiKey` stay. For the binary: `./MCPal.Bridge enroll --config mcpal.json --url https://… --code mcpale_…` saves `credentials.json` next to `mcpal.json`; document `mcpal.credentialsFile` and the key order (`MCPAL_API_KEY`, `mcpal.apiKey`, credentials file).
- Add a "Troubleshooting" table after step 3:

| Symptom | Likely cause | What to do |
|---|---|---|
| Portal never shows the bridge online | no outbound HTTPS to the MCPal server, or wrong `MCPAL_URL` | `docker logs mcpal-bridge` (or `journalctl -u mcpal-bridge`); open the server URL from that machine |
| `The enrollment code is invalid, expired or already used` | the code works once and lasts 15 minutes | create a new code on the Setup page |
| Container restarts and says it needs a key | the data volume (`/data`) was lost after the code was spent | create a new code and start again, or pass `MCPAL_API_KEY` |
| `check` says a server `FAILED to start` | the local MCP server command or URL in `mcp.json` is wrong | read the log line above it; run the server command by hand |

- [ ] **Step 2: Packaging readmes**

In `README.linux.txt` and `README.windows.txt` mention `--enroll <code>` / `-Enroll <code>` as the first choice, `--api-key` / `-ApiKey` as the alternative, and that the key lands in `credentials.json` next to `mcpal.json` (Linux: `/etc/mcpal`, Windows: `C:\ProgramData\MCPal`).

- [ ] **Step 3: Protocol doc, backlog, changelog, plan**

- `docs/tunnel-protocol.md`: new short section "Enrollment (outside the tunnel)": `POST /api/bridge/enroll`, request `{code, bridgeName}`, responses `200 {url, apiKey}` / `400 {error: "invalid_code"}` / `429`, the code is single use, 15 minutes by default, stored hashed, the key is an ordinary bridge key. State that the tunnel protocol version does not change.
- `docs/next-steps.md`: add a section "Easier setup, later slices" listing: `mcp.json` help in the Setup page (paste-and-lint box, secret extraction to `${NAME}` plus matching `-e` lines), actionable `check` error messages, bridge-side diagnostics reported to the portal (protocol 1.3: per-server issues in `BridgeCatalog`, shown on the Connections page), owner can revoke an open enrollment code.
- `CHANGELOG.md` under Unreleased: "Added": enrollment codes (Setup page, `POST /api/portal/setup/enrollments`, anonymous `POST /api/bridge/enroll`, table `BridgeEnrollments` via migration `BridgeEnrollments`, option `Mcpal__EnrollmentLifetimeMinutes`, bridge verb `enroll`, `MCPAL_ENROLL`, credentials file and `mcpal.credentialsFile`, `install.sh --enroll`, `install.ps1 -Enroll`). "Changed": the Setup page now offers enrollment by default; the manual bridge key moved under "advanced". State "Not breaking: existing keys, installs and `MCPAL_API_KEY` keep working."
- `plan.md` Status and handoff: add a short "Done (2026-10-04): enrollment codes" entry with what was built and pointing to the spec and plan.

- [ ] **Step 4: Spec touch-ups (so the spec matches what was built)**

In `docs/superpowers/specs/2026-10-04-easy-setup-design.md`:
- Status line: `Status: implemented 2026-10-04.`
- Error handling: replace "details only in server logs (counted in a `ServerTelemetry` counter by outcome: `redeemed`, `rejected`)" with "details only in the server log (information level)".
- Bridge section: replace "or an ACL for Administrators, SYSTEM and the service account (Windows, same helper idea as `install.ps1`)" with "or the ACL of the data directory that `install.ps1` sets (Windows; new files inherit it)".

- [ ] **Step 5: Commit**

```bash
cd /home/jabba/MCPal && git add README.md bridge docs CHANGELOG.md plan.md && git commit -m "docs: enrollment codes in README, protocol notes, changelog" -m $'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01YTNXq3Xy1jYdY2ZLYwN6u1'
```

---

### Task 10: Final verification

- [ ] **Step 1: Full build and all .NET tests**

Run: `cd /home/jabba/MCPal && dotnet build MCPal.slnx 2>&1 | tail -3 && dotnet test MCPal.slnx 2>&1 | tail -12`
Expected: 0 warnings; Bridge, Server and E2E all PASS. Two timing-sensitive tests (`AuditWriterTests.StopAsync_EntriesStillQueued_AreWrittenBeforeShutdownCompletes`, `LocalServerManagerTests.CallToolAsync_CallerCancels_ReturnsErrorQuicklyAndKeepsServerRunning`) may fail once under parallel load; rerun them alone and note it if they pass.

- [ ] **Step 2: SPA**

Run: `cd /home/jabba/MCPal/server/portal && npm run typecheck 2>&1 | tail -3 && npm test 2>&1 | tail -6 && npm run e2e:typecheck 2>&1 | tail -3`
Expected: all green.

- [ ] **Step 3: Scripts**

Run: `cd /home/jabba/MCPal && bash bridge/packaging/linux/test-install.sh 2>&1 | tail -3`
Expected: `install.sh tests passed`.

- [ ] **Step 4: Real flow by hand (compose)**

Run: `cd /home/jabba/MCPal && docker compose up --build -d`, sign up at `http://localhost:8080`, open Setup, click "Generate command", run the Docker command printed there against `http://localhost:8080` with `--add-host=host.docker.internal:host-gateway` and `-e MCPAL_URL=http://host.docker.internal:8080`, and watch the Setup page switch to "Bridge … is online". Then `docker rm -f mcpal-bridge` and start it again with the same command and volume: it must connect without a new code. Report what was seen; if Docker is not available, say so.

- [ ] **Step 5: Final state**

Run: `cd /home/jabba/MCPal && git status --short && git log --oneline main..HEAD`
Expected: clean tree; the commits of this plan on top of the spec commit.
