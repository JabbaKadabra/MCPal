# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MCPal is a reverse-tunnel MCP relay. A bridge inside a company network opens an outbound SignalR connection to the server. The server exposes one public MCP endpoint (`/mcp`) to claude.ai / Claude Code and relays tool calls through the tunnel to the bridge, which calls local MCP servers (stdio or HTTP). Each company (tenant) sees only its own tools; tenant isolation is a hard requirement.

`plan.md` holds the design decisions, conventions and a "Status and handoff" section (what is done, deviations from the plan, what is next). Read it before larger changes. `docs/tunnel-protocol.md` and `docs/oauth.md` describe the wire protocol and auth flows.

## Commands

Requirements: .NET 10 SDK, Docker (Testcontainers, compose), Node.js (SPA).

```bash
dotnet build MCPal.slnx                        # warnings are errors; must stay at zero
dotnet test MCPal.slnx                         # needs Docker; DB tests go inconclusive without it
dotnet build server/MCPal.Server.slnf          # one artifact only (also bridge/MCPal.Bridge.slnf); open these in the IDE
dotnet test server/MCPal.Server.Tests --filter "FullyQualifiedName~ToolNamingTests" # single class/test
dotnet run --project server/MCPal.Server       # http://localhost:8080, needs PostgreSQL (see appsettings)
./dev.sh                                       # hot reload for server + SPA: PostgreSQL (:5433), dotnet watch, Vite; open http://localhost:5173
docker compose up --build                      # server + PostgreSQL on :8080
./bridge/packaging/publish-bridge.sh [--version X.Y.Z] [rid] # single-file bridge (linux-x64, linux-arm64, win-x64) into artifacts/bridge
./bridge/packaging/package-bridge.sh X.Y.Z linux-x64 # release archive with install script into artifacts/release
bridge/packaging/linux/test-install.sh         # tests install.sh without root or systemd

# EF Core migrations (live in server/MCPal.Server.Storage/Migrations; design-time factory pins PostgreSQL)
dotnet ef migrations add <Name> --project server/MCPal.Server.Storage --output-dir Migrations

cd server/portal && npm ci
npm test                                       # vitest
npx vitest run src/pages/KeysPage.test.tsx     # single test file
npm run typecheck
npm run e2e                                    # Playwright smoke tests against a running stack (docker compose up --build; MCPAL_URL to override)
npm run dev                                    # :5173, proxies /api, /mcp, /hub, /oauth, /.well-known to :8080
npm run build                                  # writes the SPA into server/MCPal.Server/wwwroot
```

CI (`.github/workflows/ci.yml`) runs a Release build + `dotnet test`, `npm run typecheck` + `npm test` for the SPA, and a Playwright job against the compose stack. `.github/workflows/release.yml` builds and publishes a release for a `vX.Y.Z` tag (bridge archives, checksums, GHCR image).

Pitfalls seen in this repo:
- Run the whole `dotnet test MCPal.slnx` after touching a ring module (`ApplicationModule`, `StorageModule`, `InfrastructureModule`, `WebModule`), `ServerModule`, `ServerWebServices` or `Program.cs`: a registration mistake there breaks every `ServerWebApplicationFactory` test (for example `services.AddOpenTelemetry()` in the module's own `ServiceCollection` registers a fallback `IConfiguration`; `ServerWebServices` removes it).
- Minimal-API handlers take services as parameters: register every new service class in the module of its ring, or the parameter is treated as the request body and the host fails on startup.
- The Identity anti-forgery token is bound to the signed-in user: after any endpoint that signs a user in (login, signup, accepting an invitation) the SPA and `PortalClient` must fetch a new token.
- `Mcpal:DataProtectionPath` is required in Production (`McpalEnvironmentValidator`): it protects the signing keys of the caller tokens. `AddDataProtection()` lives in `ApplicationModule` (not `ServerWebServices`), so tests without web services get it too.
- Anti-forgery also guards `POST /api/oauth/authorize`; in tests the portal login cookie is persistent, so a `FakeTimeProvider` that starts far from the real clock makes the test client drop the cookie (start it at `DateTimeOffset.UtcNow` when a test signs in).
- A non-published `dotnet run` in Production serves empty bodies for compressed static assets (`MapStaticAssets` needs the publish manifest); use Development or the compose image.

## Architecture

The repo is grouped by what ships. `server/` is the container image (the onion rings `MCPal.Server.Domain`, `.Application`, `.Storage`, `.Infrastructure`, `.Web`, the ASP.NET host `MCPal.Server`, their tests, and the `portal/` SPA that is built into the host's `wwwroot`). `bridge/` is the on-prem binary (`MCPal.Bridge`, its tests, `packaging/` with publish scripts and installers). `shared/MCPal.Contracts` is the tunnel protocol both sides compile against. `tests/` holds only cross-artifact tests. One `MCPal.slnx` holds everything, because Contracts changes and the E2E tests must build both sides together. Use the `.slnf` filters for a single artifact.

Naming: the **server** is MCPal's own cloud service and the **bridge** is the process inside the company network. The MCP servers the bridge talks to are **local MCP servers**. In prose, say "MCPal server" wherever "server" alone could mean a local MCP server.

Projects:
- `shared/MCPal.Contracts`: tunnel DTOs and hub interfaces. No MCP SDK dependency; tool schemas and content travel as JSON strings. `ProtocolVersion` major must match between bridge and server (current 1.2; `UserContextMinor` gates `CallToolRequest.User`, `CancelCallMinor` gates `CancelCall`). `ToolPattern` is the shared glob.
- `server/`: the server is an onion of six projects. A project references only projects inside it; `ArchitectureTests` fails when a ring references a forbidden assembly (for example Application → Npgsql or Domain → EF Core). Namespaces did not follow the split: a feature keeps its namespace (`MCPal.Server.Tenancy`, `.Access`, ...) in every ring, so a feature folder exists in several projects. Types stay `internal`; `server/Directory.Build.props` makes the rings' internals visible to each other and to the tests.
  - `MCPal.Server.Domain` (centre; references Contracts only): entities and enums, pure rules (`AccessEvaluator` inputs such as `CompanyPolicy`, `ToolNaming`, `RedirectUriPolicy`, `McpalClaims`, `ApiKeyQueries`), `McpalOptions`, and the ports the adapters implement: `IMcpalData` (queryable sets, add/remove, save, transactions; `IDataTransaction.LockAsync` is the Postgres advisory lock), `IAuditSearch` (provider-specific text search), `IEmailSender`. `PortalUser : IdentityUser` makes Domain reference `Microsoft.Extensions.Identity.Stores`.
  - `MCPal.Server.Application`: services, background services, `CallRelay`, `ConnectionRegistry`, `ServerTelemetry`, `IBridgeInvoker`, `ApplicationModule`. Reaches the database only through `IMcpalData`; it references `Microsoft.EntityFrameworkCore` (not Npgsql) for async LINQ operators, `ExecuteUpdateAsync` and `ExecuteDeleteAsync`.
  - `MCPal.Server.Storage` (adapter): `MCPalDbContext` (implements `IMcpalData`), migrations, `DatabaseMigrator`, `PostgresAuditSearch`, ASP.NET Identity stores and the database health check (web host only), `StorageModule`.
  - `MCPal.Server.Infrastructure` (adapter): `SmtpEmailSender` (MailKit), `LogEmailSender`, `InfrastructureModule`.
  - `MCPal.Server.Web`: all `*Endpoints`, `McpBearerAuthenticationHandler`, `TenantToolHandlers`, `BridgeHub`, `HubBridgeInvoker`, `ServerWebServices`, `WebModule`. References Application and Domain, never Storage or Infrastructure.
  - `MCPal.Server` (host): `Program.cs`, `ServerModule` (composes the four modules; Storage first, because hosted services start in registration order and the migration must run first), Dockerfile, `wwwroot`.
  - Features, by ring (read a feature name below as its files in all rings):
    - `Tunnel/`: `BridgeHub` at `/hub/bridge`. `ConnectionRegistry` is an in-memory immutable snapshot updated by compare-and-swap (no locks); it is why the MVP is single-instance. `ToolNaming` builds public names `sanitize(server)__sanitize(tool)` (max 64 chars, hash suffix on truncation). `TunnelSweeper`/`TunnelTerminator` close tunnels whose key was revoked/expired or whose company was disabled.
    - `Mcp/`: stateless MCP HTTP transport. `ConfigureSessionOptions` runs per request and `TenantToolHandlers` installs list/call handlers bound to the company from the authenticated principal. `CallRelay` forwards calls to the bridge via SignalR client results (`ISingleClientProxy.InvokeAsync`).
    - `OAuth/`: own minimal OAuth 2.1 authorization server (DCR, PKCE S256, refresh, RFC 9728/8414 metadata), not OpenIddict and not the SDK's `AddMcp` auth extension.
    - `Tenancy/`: companies, API keys (hashed), `McpBearerAuthenticationHandler`, claims. Identity is the **portal user**; there are no service accounts. Two API key purposes (`ApiKeyPurpose`): `Personal` (personal access token bound to a user via `ApiKey.UserId`, works on `/mcp`, `AuthKind=pat`) and `Bridge` (company key, no user, opens tunnels only, `AuthKind=apikey`). OAuth tokens (`AuthKind=oauth`) belong to the user who signed in; the OAuth sign-in is the portal login only (no pasted keys). `/mcp` requires the `UserId` claim, tunnels require a bridge key; tunnels never accept OAuth tokens. `ApiKeys.Active` and `Users.Active` (in `ApiKeyQueries`) are the one definition of a usable key/user (not disabled, same company, company enabled). `CallerIdentity` (company, user, auth kind, key, OAuth client) is built from the principal and is what `TenantToolHandlers`/`CallRelay` use.
    - `Access/`: groups, grants and the caller token. `AccessGroup` (one implicit `Everyone` per company), `AccessGroupMember`, `AccessGrant` (server glob, case-insensitive, plus tool globs, case-sensitive; `MCPal.Contracts.ToolPattern`). `AccessPolicyLoader` builds a `CompanyPolicy` (company slug + `UserPolicy` per active user, owners may use everything); `AccessPolicyCache` is a singleton, lock-free (generation counter per company, TTL 5 min, `Invalidate` on every change in `AccessService`/`TeamService`/signup/display name). `TenantToolHandlers` filters `tools/list` and `CallRelay` refuses forbidden tools exactly like unknown ones. `Access/UserContext/`: `SigningKeyStore` (ES256 keys in the database, private part protected by Data Protection, immutable `KeySet` swapped with `Interlocked.Exchange`, Postgres advisory lock for rotation), `SigningKeyRotationService` (hourly), `UserContextIssuer` (JWT per call, `typ` `mcpal-user+jwt`, audience `mcpal:<slug>/<server>`), `JwksEndpoints` (`/.well-known/jwks.json`). The caller reaches the bridge in `CallToolRequest.User` (protocol 1.2). Design and trust model: `docs/access-control.md`.
    - `Audit/`: tool call audit log. `CallRelay` enqueues one `ToolCallAudit` per call into `AuditWriter` (bounded channel, background insert, drops and counts when full); `AuditRetention`/`AuditCleanupService` delete old rows; `AuditEndpoints` serve `/api/portal/audit` (owners only; rows carry the `UserId`, no foreign key). `Diagnostics/ServerTelemetry.cs` holds the OpenTelemetry meters and spans and the shared `ToolCallOutcome` values.
    - `Portal/`: JSON API for the SPA, ASP.NET Identity cookie auth, antiforgery header `X-CSRF-TOKEN`. `ActiveUserFilter` (401 for a disabled user or company, checked in the database on every call) and `OwnerOnlyFilter` guard the endpoint groups; `TeamService` owns invitations, disabling, enabling and removing users (revokes tokens, protects the last active owner, invalidates the access policy cache).
    - `Storage/`: `MCPalDbContext`, migrations, `DatabaseMigrator` (runs at startup when `Mcpal:MigrateOnStartup`); all in `MCPal.Server.Storage`.
    - `Program.cs` maps everything. The SPA fallback regex must never capture `api`, `mcp`, `health`, `hub`, `.well-known`, `oauth/token`, `oauth/register`. Health endpoints: `/health/live`, `/health/ready` (DB check), `/health` (= ready).
- `bridge/MCPal.Bridge`: generic host with verbs `run` (tunnel), `check` (start local servers, list tools, exit) and `status` (print the `statusFile` a running bridge writes for monitoring). `LocalServerManager` owns MCP client connections to local servers; `TunnelClient` handles connect/reconnect with backoff and re-`Register` on reconnect, `tools/list_changed` and a 30 s refresh. Config is `mcpal.json` (`mcpal`, `serverOptions` for MCPal-only per-server settings) plus the local servers in `mcp.json` next to it (Claude Code `.mcp.json` shape, or `mcpal.mcpServersFile`; an inline `mcpServers` block still works, a name must be in one place only); API key (a bridge key) from `MCPAL_API_KEY` or `mcpal.apiKey`. Per server `userContext` (default true) and `userTokenHeader` (HTTP), top-level `jwksFile`: `UserContextMeta` puts the caller into `_meta["eu.nordstein.mcp/user"]` (and strips a spoofed key), `UserTokenHeaderHandler` + `UserTokenScope` (AsyncLocal, set only around `SendRequestAsync`, after `GetClientAsync`) add the header to tool calls only, `JwksFileWriter` keeps the JWKS copy. Runs as Windows service or systemd unit.
- `server/portal`: React + TypeScript + Vite SPA (portal and the OAuth sign-in page), TanStack Query, strings via typed `t()` in `src/i18n`.

Timeouts are layered: bridge per-call limit 110 s (`callTimeoutSeconds`) sits just below the server's 120 s (`Mcpal:ToolCallTimeoutSeconds`). Keep that ordering.

## Conventions

The Nordstein C# conventions apply (skills `nordstein-code-basics`, `nordstein-code-csharp`). Project-specific points:
- DI is Autofac. All registrations live in the owning project's module (`ApplicationModule`, `StorageModule`, `InfrastructureModule`, `WebModule`, composed by the host's `ServerModule`; `BridgeModule`); hosts and tests register only the top module. `ServerModule(registerWebServices: false)` gives application, storage and infrastructure services without the ASP.NET pipeline, background services, Identity stores and health check. ASP.NET pipeline services are in `ServerWebServices.cs`.
- Package versions only in `Directory.Packages.props`; `PackageReference` items carry no version.
- `internal` by default; public only for interfaces, Contracts DTOs and the modules. `InternalsVisibleTo` exposes Server/Bridge to their test projects and E2E.
- No `!`, `null!`, `default!` or `#pragma warning disable` for nullability (only exception: guard tests passing `null!`, marked `// guard test`).
- Time only from injected `TimeProvider` (tests use `FakeTimeProvider`). Timestamps are `DateTimeOffset`. Every async method takes and forwards a `CancellationToken`.
- No `lock` / raw `SemaphoreSlim` in feature code; the bridge's `Concurrency/AsyncLock` is the only one.
- Options via `IOptions<McpalOptions>` with `ValidateDataAnnotations().ValidateOnStart()`; env vars use `__` (e.g. `Mcpal__PublicUrl`).
- Entities are currently internal mutable classes (in Domain, `Tenancy/Entities.cs`, `OAuth/Entities.cs`) that EF maps directly; `MCPalDbContext` configures them. Services query through `IMcpalData.Query<T>()`, never through `MCPalDbContext`. No global query filters: every query must filter by company explicitly.

## Tests

- NUnit 5 + NSubstitute + AwesomeAssertions (global usings in each test csproj). Names `Subject_Condition_ExpectedOutcome`; no instance fields or `[SetUp]` state; resolve the SUT from the container.
- `ServerTestBase.GetServicesAsync()` builds a fresh Autofac container from the production module with a `FakeTimeProvider` and a per-test PostgreSQL database cloned from a migrated template (`PostgresFixture`, one Testcontainers instance per run).
- `ServerWebApplicationFactory` + `FakeBridge` cover HTTP/hub integration. `SeedCompanyAsync` returns a `SeededCompany` with an owner, a bridge key (`BridgeKey`, for tunnels) and the owner's personal access token (`PersonalKey`, for `/mcp`); `SeedUserAsync` adds a member with their own token; `IssueAccessTokenAsync` stores an OAuth token. `tests/MCPal.E2E.Tests` runs the server host, a real bridge, `tests/MCPal.TestMcpServer` (stdio server with `echo`, `add`, `slow`, `fail`, `crash`, `whoami`) and an in-process HTTP MCP server (`HttpLocalMcpServer`) behind an SDK MCP client.
