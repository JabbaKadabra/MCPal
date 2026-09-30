# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MCPal is a reverse-tunnel MCP relay. An agent inside a company network opens an outbound SignalR connection to the cloud. The cloud exposes one public MCP endpoint (`/mcp`) to claude.ai / Claude Code and relays tool calls through the tunnel to the agent, which calls local MCP servers (stdio or HTTP). Each company (tenant) sees only its own tools; tenant isolation is a hard requirement.

`plan.md` holds the design decisions, conventions and a "Status and handoff" section (what is done, deviations from the plan, what is next). Read it before larger changes. `docs/tunnel-protocol.md` and `docs/oauth.md` describe the wire protocol and auth flows.

## Commands

Requirements: .NET 10 SDK, Docker (Testcontainers, compose), Node.js (SPA).

```bash
dotnet build MCPal.sln                         # warnings are errors; must stay at zero
dotnet test MCPal.sln                          # needs Docker; DB tests go inconclusive without it
dotnet test tests/MCPal.Cloud.Tests --filter "FullyQualifiedName~ToolNamingTests"   # single class/test
dotnet run --project src/MCPal.Cloud           # http://localhost:8080, needs PostgreSQL (see appsettings)
docker compose up --build                      # cloud + PostgreSQL on :8080
./scripts/publish-agent.sh [--version X.Y.Z] [rid]   # single-file agent (linux-x64, linux-arm64, win-x64) into artifacts/agent
./scripts/package-agent.sh X.Y.Z linux-x64      # release archive with install script into artifacts/release
packaging/linux/test-install.sh                 # tests install.sh without root or systemd

# EF Core migrations (live in src/MCPal.Cloud/Storage/Migrations; design-time factory pins PostgreSQL)
dotnet ef migrations add <Name> --project src/MCPal.Cloud --output-dir Storage/Migrations

cd src/MCPal.Web && npm ci
npm test                                       # vitest
npx vitest run src/pages/KeysPage.test.tsx     # single test file
npm run typecheck
npm run e2e                                    # Playwright smoke tests against a running stack (docker compose up --build; MCPAL_URL to override)
npm run dev                                    # :5173, proxies /api, /mcp, /hub, /oauth, /.well-known to :8080
npm run build                                  # writes the SPA into src/MCPal.Cloud/wwwroot
```

CI (`.github/workflows/ci.yml`) runs a Release build + `dotnet test`, `npm run typecheck` + `npm test` for the SPA, and a Playwright job against the compose stack. `.github/workflows/release.yml` builds and publishes a release for a `vX.Y.Z` tag (agent archives, checksums, GHCR image).

Pitfalls seen in this repo:
- Run the whole `dotnet test MCPal.sln` after touching `CloudModule`, `CloudWebServices` or `Program.cs`: a registration mistake there breaks every `CloudWebApplicationFactory` test (for example `services.AddOpenTelemetry()` in the module's own `ServiceCollection` registers a fallback `IConfiguration`; `CloudWebServices` removes it).
- Minimal-API handlers take services as parameters: register every new service class in `CloudModule`, or the parameter is treated as the request body and the host fails on startup.
- The Identity anti-forgery token is bound to the signed-in user: after any endpoint that signs a user in (login, signup, accepting an invitation) the SPA and `PortalClient` must fetch a new token.
- A non-published `dotnet run` in Production serves empty bodies for compressed static assets (`MapStaticAssets` needs the publish manifest); use Development or the compose image.

## Architecture

Projects:
- `src/MCPal.Contracts`: tunnel DTOs and hub interfaces. No MCP SDK dependency; tool schemas and content travel as JSON strings. `ProtocolVersion` major must match between agent and cloud.
- `src/MCPal.Cloud`: single ASP.NET Core project with feature folders (no Domain/Application/Storage project split):
  - `Tunnel/`: `AgentHub` at `/hub/agent`. `ConnectionRegistry` is an in-memory immutable snapshot updated by compare-and-swap (no locks); it is why the MVP is single-instance. `ToolNaming` builds public names `sanitize(server)__sanitize(tool)` (max 64 chars, hash suffix on truncation). `TunnelSweeper`/`TunnelTerminator` close tunnels whose key was revoked/expired or whose company was disabled.
  - `Mcp/`: stateless MCP HTTP transport. `ConfigureSessionOptions` runs per request and `TenantToolHandlers` installs list/call handlers bound to the company from the authenticated principal. `CallRelay` forwards calls to the agent via SignalR client results (`ISingleClientProxy.InvokeAsync`).
  - `OAuth/`: own minimal OAuth 2.1 authorization server (DCR, PKCE S256, refresh, RFC 9728/8414 metadata), not OpenIddict and not the SDK's `AddMcp` auth extension.
  - `Tenancy/`: companies, API keys (hashed), `McpBearerAuthenticationHandler`, claims. One credential type, the API key, with a purpose (`ApiKeyPurpose`: `Agent` opens tunnels only, `Client` works on `/mcp` and for the OAuth login only, `Any` = both, the value of keys from before purposes existed) and an optional `AllowedServers` list (`Client`/`Any`; empty = all servers). Purpose and servers travel as claims (`KeyPurpose`, `AllowedServer`); OAuth tokens inherit them from their key. Tunnels accept only API keys (`AuthKind=apikey`), never OAuth access tokens. `CallerIdentity` (company, auth kind, key, OAuth client, allowed servers) is built from the principal and is what `TenantToolHandlers`/`CallRelay` use; tools outside the server scope answer like unknown tools.
  - `Audit/`: tool call audit log. `CallRelay` enqueues one `ToolCallAudit` per call into `AuditWriter` (bounded channel, background insert, drops and counts when full); `AuditRetention`/`AuditCleanupService` delete old rows; `AuditEndpoints` serve `/api/portal/audit`. `Diagnostics/CloudTelemetry.cs` holds the OpenTelemetry meters and spans and the shared `ToolCallOutcome` values.
  - `Portal/`: JSON API for the SPA, ASP.NET Identity cookie auth, antiforgery header `X-CSRF-TOKEN`.
  - `Storage/`: `MCPalDbContext`, migrations, `DatabaseMigrator` (runs at startup when `Mcpal:MigrateOnStartup`).
  - `Program.cs` maps everything. The SPA fallback regex must never capture `api`, `mcp`, `health`, `hub`, `.well-known`, `oauth/token`, `oauth/register`. Health endpoints: `/health/live`, `/health/ready` (DB check), `/health` (= ready).
- `src/MCPal.Agent`: generic host with verbs `run` (tunnel), `check` (start local servers, list tools, exit) and `status` (print the `statusFile` a running agent writes for monitoring). `LocalServerManager` owns MCP client connections to local servers; `TunnelClient` handles connect/reconnect with backoff and re-`Register` on reconnect, `tools/list_changed` and a 30 s refresh. Config is `mcpal.json` (`cloud` + a Claude-style `mcpServers` block); API key from `MCPAL_API_KEY` or `cloud.apiKey`. Runs as Windows service or systemd unit.
- `src/MCPal.Web`: React + TypeScript + Vite SPA (portal and the OAuth sign-in page), TanStack Query, strings via typed `t()` in `src/i18n`.

Timeouts are layered: agent per-call limit 110 s (`callTimeoutSeconds`) sits just below the cloud's 120 s (`Mcpal:ToolCallTimeoutSeconds`). Keep that ordering.

## Conventions

The Nordstein C# conventions apply (skills `nordstein-code-basics`, `nordstein-code-csharp`). Project-specific points:
- DI is Autofac. All registrations live in the owning project's `Module.cs` (`CloudModule`, `AgentModule`); hosts and tests register only the module. `CloudModule(registerWebServices: false)` gives storage and domain services without the ASP.NET pipeline. ASP.NET pipeline services are in `CloudWebServices.cs`.
- Package versions only in `Directory.Packages.props`; `PackageReference` items carry no version.
- `internal` by default; public only for interfaces, Contracts DTOs and the modules. `InternalsVisibleTo` exposes Cloud/Agent to their test projects and E2E.
- No `!`, `null!`, `default!` or `#pragma warning disable` for nullability (only exception: guard tests passing `null!`, marked `// guard test`).
- Time only from injected `TimeProvider` (tests use `FakeTimeProvider`). Timestamps are `DateTimeOffset`. Every async method takes and forwards a `CancellationToken`.
- No `lock` / raw `SemaphoreSlim` in feature code; the agent's `Concurrency/AsyncLock` is the only one.
- Options via `IOptions<McpalOptions>` with `ValidateDataAnnotations().ValidateOnStart()`; env vars use `__` (e.g. `Mcpal__PublicUrl`).
- Entities are currently internal mutable classes used by EF directly (`Tenancy/Entities.cs`, `OAuth/Entities.cs`). No global query filters: every query must filter by company explicitly.

## Tests

- NUnit 5 + NSubstitute + AwesomeAssertions (global usings in each test csproj). Names `Subject_Condition_ExpectedOutcome`; no instance fields or `[SetUp]` state; resolve the SUT from the container.
- `CloudTestBase.GetServicesAsync()` builds a fresh Autofac container from the production module with a `FakeTimeProvider` and a per-test PostgreSQL database cloned from a migrated template (`PostgresFixture`, one Testcontainers instance per run).
- `CloudWebApplicationFactory` + `FakeAgent` cover HTTP/hub integration; `tests/MCPal.E2E.Tests` runs the cloud host, a real agent and `tests/MCPal.TestMcpServer` (stdio server with `echo`, `add`, `slow`, `fail`, `crash`) behind an SDK MCP client.
