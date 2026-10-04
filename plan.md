# MCPal — reverse-tunnel MCP relay: design spec + implementation plan

Date: 2026-09-29, updated 2026-09-30. Status: design approved in brainstorming (sections 1–9, with "Bridge" entity dropped in favour of API-key-authenticated tunnels). **MVP implemented (phases 1–11), tested and committed.** See [Status and handoff](#status-and-handoff) at the end.

## Context

Companies have internal knowledge bases reachable as MCP servers (stdio or HTTP) or as Claude skills. To use them from claude.ai (web/Desktop/mobile/Cowork) the MCP server must be reachable **inbound** from Anthropic's egress range (`160.79.104.0/21`). An inbound public port is a KO criterion for many companies.

MCPal removes the inbound requirement: the company runs a small **bridge** process that opens an **outbound** persistent connection to the MCPal server. The server exposes one public MCP endpoint. claude.ai talks to the server; the server forwards tool calls over the tunnel to the bridge, which calls the local MCP servers. Zero inbound ports, zero changes to existing MCP servers.

Success: a company registers, creates an API key, drops its existing `mcpServers` config block into `mcpal.json`, starts the bridge, adds one custom connector in claude.ai, and sees only its own tools. Setup under 15 minutes. **Data isolation between companies is a hard requirement.**

Research findings that shaped the design (official docs, verified 2026-09-29):
- claude.ai custom connectors: OAuth (DCR/CIMD, PKCE S256) supported by default. Static request headers (`static_headers`, e.g. `Authorization: Bearer …`) are **beta, limited to some organizations**. ⇒ we must offer OAuth; bearer API key is a fallback. Source: https://claude.com/docs/connectors/building/authentication
- OAuth: 401 with `WWW-Authenticate: Bearer resource_metadata="…"` starts sign-in; RFC 9728 + RFC 8414 metadata; token endpoint must accept form-urlencoded; discovery/token ≤10 s, refresh ≤30 s; bad refresh → `invalid_grant`; redirect URIs `https://claude.ai/api/mcp/auth_callback` and loopback (any port, `localhost` and `127.0.0.1`) for Claude Code.
- Team/Enterprise: only Owners add custom connectors; members connect individually.
- OpenIddict has no DCR yet (issue #2404, planned 8.0 preview) ⇒ build a minimal OAuth 2.1 AS ourselves.
- ModelContextProtocol C# SDK: latest stable **2.2.0** (net8/9/10). `HttpServerTransportOptions.Stateless`, `ConfigureSessionOptions` (runs per request in stateless mode → per-tenant handlers), `McpServerOptions.Handlers.ListToolsHandler/CallToolHandler`, MCP auth extension (`AddMcp` on `AuthenticationBuilder`, `McpAuthenticationOptions.ResourceMetadata`) emitting the 401 challenge. Client: `McpClient.CreateAsync`, `StdioClientTransport(StdioClientTransportOptions)`, `HttpClientTransport(HttpClientTransportOptions)`, `ListToolsAsync`, `CallToolAsync`, `NotificationMethods.ToolListChangedNotification`. (Names verified on 1.2.0 XML docs; re-verify against 2.2.0 when starting — v2 made stateless the default and renamed some members.)
- SignalR: server→client invocation with result (`ISingleClientProxy.InvokeAsync<T>` / typed `IHubContext<THub, T>` with `Task<T>` methods) exists since .NET 7; works only when the caller runs on the instance holding the connection ⇒ fine for single-instance MVP.
- Existing repo `Translogica.AI.Agents` (not reused, standalone decision) is a useful reference: its `docs/cloud-mcp-tools.md` / `docs/api-authentication.md` document pitfalls (plaintext API keys — we hash; tool exceptions must surface `Message` only; rate limiting the MCP path because MCP middleware short-circuits the pipeline).

## Decisions (from brainstorming)

| Topic | Decision |
|---|---|
| Product / repo | **MCPal**, standalone repo at `/home/jabba/MCPal`, remote `github.com/JabbaKadabra/MCPal`, branch `main` |
| Stack | All .NET 10: ASP.NET Core server, .NET worker bridge |
| DI container | **Autofac** (decided 2026-09-30). One `Module` subclass per project in `Module.cs` (`ServerModule`, `BridgeModule`); hosts use `AutofacServiceProviderFactory` and register only the module. |
| Project layout | **Onion rings** (changed 2026-10-04; was one `MCPal.Server` project with feature folders): `MCPal.Server.Domain`, `.Application`, `.Storage`, `.Infrastructure`, `.Web` and the host `MCPal.Server`; see `docs/superpowers/specs/2026-10-04-server-onion-design.md`. Nordstein C# conventions apply otherwise (see below). |
| Package versions | Central package management in `Directory.Packages.props`; `PackageReference` items carry no version. |
| Tests | NUnit 5 + NSubstitute + AwesomeAssertions (`x.Should()…`), global usings set in each test csproj. Integration via `Microsoft.AspNetCore.Mvc.Testing` `WebApplicationFactory<Program>`. |
| License | Elastic License 2.0 (`LICENSE`, `PackageLicenseExpression=Elastic-2.0`) |
| Tunnel | SignalR (WebSockets + SSE/long-poll fallback → corporate-proxy friendly, auto-reconnect) |
| Relay model | **Tool-level**: server is the MCP server, bridge is an MCP client; tunnel carries `Register/ToolsChanged` + `CallTool` |
| Tenancy | Company = tenant; many tunnels, many API keys |
| Credential | **One credential type: API key.** Used both by the bridge (tunnel) and by Claude (bearer / OAuth login). No scopes, no Bridge entity. |
| Auth to Claude | OAuth 2.1 AS built in (DCR, PKCE) **plus** bearer API key fallback |
| Onboarding | Minimal web portal as **React SPA** (Vite + TypeScript) on a JSON API; ASP.NET Identity (cookie) behind it |
| DB | EF Core + PostgreSQL (Testcontainers for tests) |
| Hosting | Docker, Linux, single instance for MVP (scale-out later) |
| Out of scope | Skill→MCP generator (sub-project 3), backplane/scale-out, MCP resources/prompts, CIMD, installer packaging, billing |

## Architecture

```
claude.ai / Claude Code                     Company network (no inbound ports)
   │  HTTPS  streamable HTTP (stateless)        ┌──────────────────────────────┐
   ▼                                            │  MCPal.Bridge                 │
┌──────────────────────── MCPal.Server ───────┐  │   ├ McpClient(stdio) ─► kb   │
│ /mcp  (MCP server, per-tenant handlers)    │  │   ├ McpClient(http)  ─► wiki │
│ /oauth/*, /.well-known/*  (OAuth 2.1 AS)   │  │   └ SignalR client ──────────┼──► outbound wss
│ /hub/bridge (SignalR hub, API-key auth) ◄───┼──┘                              │
│ /api/portal/* JSON (Identity cookie)       │
│ /  React SPA static files (wwwroot)        │
│ ConnectionRegistry (in-memory, per company)│
│ EF Core → PostgreSQL                       │
└────────────────────────────────────────────┘
```

Call flow `tools/call`: Claude → `POST /mcp` → McpBearer auth → `CompanyId` claim → `ConnectionRegistry[company]` finds the connection owning `server` → `hub.Clients.Single(connId).InvokeAsync<CallToolResponse>(…)` with timeout → bridge `McpClient.CallToolAsync` → local server → response back as `CallToolResult`.

Call flow `tools/list`: served from the registry cache; no round trip.

## Repo layout

```
MCPal/                             grouped by what ships: server/ (container image), bridge/ (on-prem binary)
  MCPal.slnx                       (solution folders server/, bridge/, shared/, tests/)
  Directory.Build.props            (net10.0, nullable, warnings-as-errors, LangVersion latest, AnalysisLevel latest-recommended,
                                    EnforceCodeStyleInBuild, ImplicitUsings; *.Tests projects get IsTestProject)
  Directory.Packages.props         (central package versions)
  LICENSE                          (Elastic License 2.0)
  .dockerignore, .gitignore        (.gitignore also ignores server/MCPal.Server/wwwroot/ and mcpal.json)
  docker-compose.yml               server + postgres
  README.md, docs/
  shared/MCPal.Contracts/          tunnel DTOs + hub interfaces; no SDK dependency
  server/
    MCPal.Server.slnf              solution filter: server, its tests, contracts
    MCPal.Server/                  ASP.NET Core
       Tenancy/      Company, ApiKey, ApiKeyService (hash/validate), McpBearer auth scheme
       Tunnel/       BridgeHub, ConnectionRegistry, ToolNaming, TunnelSweeper
       Mcp/          TenantToolHandlers, CallRelay
       OAuth/        metadata endpoints, DCR, authorize (JSON API for the SPA page), token, stores
       Portal/       minimal-API JSON endpoints: auth (signup/login/logout/me), keys, connections, team
       Audit/, Diagnostics/
       Storage/      MCPalDbContext, migrations
       wwwroot/      built SPA output (written by the portal build; SPA fallback to index.html)
       Program.cs, Module.cs (Autofac ServerModule), appsettings.json, Dockerfile (build context = repo root)
    MCPal.Server.Tests/            unit + WebApplicationFactory integration (Testcontainers Postgres)
    portal/                        React + TypeScript + Vite SPA (portal + OAuth authorize page), ships inside the server image
       src/pages/, src/api/ (typed fetch client), e2e/ (Playwright)
       vite.config.ts (dev proxy → server on :8080), package.json
  bridge/
    MCPal.Bridge.slnf              solution filter: bridge, its tests, contracts, test MCP server
    MCPal.Bridge/                  worker: Config/, Local/ (McpClient per server), Tunnel/ (SignalR client), Status/, Diagnostics/,
                                   Program.cs, Module.cs (Autofac BridgeModule)
    MCPal.Bridge.Tests/            config parsing, local server manager, status
    packaging/                     publish/package scripts, linux/ (install.sh, systemd unit), windows/ (install.ps1)
  tests/
    MCPal.E2E.Tests/               server host + in-process bridge + TestMcpServer + real SDK McpClient
    MCPal.TestMcpServer/           stdio MCP server (SDK) with `echo`, `add`, `slow`, `fail` tools
```

## Components

### MCPal.Contracts (tunnel protocol)
- `IBridgeHubClient` (server → bridge, result-returning): `Task<CallToolResponse> CallTool(CallToolRequest request)`.
- Hub methods (bridge → server): `Task<RegisterResult> Register(BridgeCatalog catalog)`, `Task ToolsChanged(BridgeCatalog catalog)`.
- DTOs (records, System.Text.Json): `BridgeCatalog { BridgeName, BridgeVersion, ServerCatalog[] Servers }`, `ServerCatalog { Name, ToolDescriptor[] Tools }`, `ToolDescriptor { Name, Title?, Description?, InputSchemaJson (string), AnnotationsJson? }`, `CallToolRequest { RequestId, ServerName, ToolName, ArgumentsJson }`, `CallToolResponse { IsError, ContentJson /* serialized CallToolResult */, ErrorMessage? }`, `RegisterResult { Accepted, RejectedServers[] with reason }`.
- Schema/content travel as JSON strings so Contracts stays SDK-free and SignalR serialization stays trivial.
- `ProtocolVersion` constant sent in `Register`; server rejects unknown majors with a clear message.

### MCPal.Server / Tenancy
- Entities: `Company { Id, Name, Slug, CreatedAt, Disabled }`, `ApiKey { Id, CompanyId, Name, Prefix, KeyHash, CreatedAt, ExpiresAt?, Disabled, LastUsedAt? }`, `PortalUser : IdentityUser { CompanyId }`.
- Key format `mcpal_<8 char company short id>_<40 random [A-Za-z0-9]>` from `RandomNumberGenerator`; stored SHA-256 (`KeyHash`), `Prefix` = first 14 chars for display. Shown once.
- `IApiKeyService.ValidateAsync(rawKey) → ValidatedKey { CompanyId, ApiKeyId } | null` (checks hash, Disabled, ExpiresAt, company Disabled; updates LastUsedAt throttled).
- `McpBearerAuthenticationHandler` (scheme `McpBearer`): reads only `Authorization: Bearer`; value starting with `mcpal_` → API key path; otherwise → OAuth access-token path. Principal claims: `CompanyId`, `ApiKeyId`, `AuthKind` (`apikey`|`oauth`). Never read tokens from query string.
- Revoking a key: disables tunnels authenticated with it (registry drops them, hub aborts connections) and invalidates OAuth tokens bound to it.

### MCPal.Server / Tunnel
- `BridgeHub : Hub<IBridgeHubClient>` at `/hub/bridge`, `[Authorize(AuthenticationSchemes = "McpBearer")]`. Company/key come from the principal, **never** from the payload.
- `ConnectionRegistry` (singleton): `CompanyId → { ConnectionId → ConnectionInfo { BridgeName, ApiKeyId, ConnectedAt, Servers: Dictionary<serverName, ToolDescriptor[]> } }` plus `CompanyId → serverName → ConnectionId` index. Thread-safe; `OnDisconnectedAsync` removes.
- Server names unique per company: `Register` rejects servers whose name is held by another live connection (`RejectedServers`), accepts the rest. Same connection re-registering replaces its own entries.
- SignalR options: `MaximumReceiveMessageSize = 10 MB`, `ClientTimeoutInterval = 60 s`, `KeepAliveInterval = 15 s`, JSON protocol.
- `ToolNaming.Public(server, tool)` = `sanitize(server) + "__" + sanitize(tool)`, sanitize → `[A-Za-z0-9_-]`, total ≤ 64 chars (truncate + 6-char hash suffix when needed); `TryParse(publicName)` via registry map (store the mapping at registration instead of parsing).

### MCPal.Server / Mcp
- `MapMcp("/mcp")`, `Stateless = true`, `RequireAuthorization("McpBearer")`, `AddMcp` authentication with `ResourceMetadata` (`resource = https://<PublicUrl>/mcp`, `authorization_servers = [https://<PublicUrl>]`, `scopes_supported = ["mcp"]`, `bearer_methods_supported = ["header"]`).
- `ConfigureSessionOptions`: reads `CompanyId` from `HttpContext.User`, sets `ServerInfo`, `Capabilities.Tools` (no list_changed in stateless mode), `Handlers.ListToolsHandler` = registry snapshot mapped to `Tool` (name = public name, `InputSchema` parsed from JSON string, description prefixed with `[server]`), `Handlers.CallToolHandler` = `CallRelay.CallAsync(company, publicName, args, ct)`.
- `CallRelay`: resolves public name → (connectionId, server, tool); missing → `CallToolResult { IsError, "Tool not available (bridge offline)" }`; invokes hub with `Mcpal:ToolCallTimeoutSeconds` (default 120) linked to request cancellation; maps `CallToolResponse` → `CallToolResult` (deserialize `ContentJson`); exceptions → `IsError` with safe message, full detail logged with CompanyId/connectionId.
- Rate limiting: partitioned fixed window per bearer-token hash on `/mcp` only (`app.UseRateLimiter()` before `MapMcp`).
- `ServerInstructions`: short text telling the model tools are grouped by `server__tool`.

### MCPal.Server / OAuth (minimal OAuth 2.1 AS)
- Endpoints: `GET /.well-known/oauth-protected-resource/mcp` (handled by SDK auth extension or our own minimal endpoint), `GET /.well-known/oauth-authorization-server` (issuer = PublicUrl, `authorization_endpoint`, `token_endpoint`, `registration_endpoint`, `response_types_supported=["code"]`, `grant_types_supported=["authorization_code","refresh_token"]`, `code_challenge_methods_supported=["S256"]`, `token_endpoint_auth_methods_supported=["none"]`, `scopes_supported=["mcp"]`).
- `POST /oauth/register` (RFC 7591): accept `redirect_uris`, `client_name`, `token_endpoint_auth_method=none`; validate each redirect URI against allowlist (`https://claude.ai/api/mcp/auth_callback`, and `http://localhost/*` / `http://127.0.0.1/*` with any port); persist `OAuthClient { ClientId (random), RedirectUris[], ClientName, CreatedAt }`; return 201 JSON. No secret (public client).
- `GET /oauth/authorize?…` is served by the SPA (fallback `index.html`, React route `OAuthAuthorize`). The page calls `GET /api/oauth/authorize/context?<same query>` which validates `client_id`, `redirect_uri` (exact match or loopback port-agnostic), `response_type=code`, `code_challenge` + `code_challenge_method=S256` (required), `state`, `resource` (must equal `<PublicUrl>/mcp` when present), `scope`, and returns `{ clientName, signedInCompany? }` or an error. The page shows connector name + form: **paste API key** (primary) or, when a portal user is signed in, a "Connect as <company>" button; submits `POST /api/oauth/authorize` (JSON, anti-forgery) which on success creates `AuthorizationCode { Code (random, hashed), ClientId, CompanyId, ApiKeyId?, RedirectUri, CodeChallenge, Scope, Resource, ExpiresAt = +5 min, Used }` and returns `{ redirectUrl }` (redirect URI + `code` + `state`); the SPA navigates there. Errors → `{ redirectUrl }` with `error=…` when the redirect URI is valid, else an error payload the page renders.
- `POST /oauth/token` (form-urlencoded only): `authorization_code` → verify code (single use, not expired, client matches, redirect_uri matches, PKCE `S256(code_verifier) == code_challenge`) → issue `access_token` (opaque random, hashed, 1 h) + `refresh_token` (opaque, hashed, 30 d) stored as `OAuthToken { Hash, Kind, CompanyId, ApiKeyId?, ClientId, ExpiresAt, Revoked }`; `refresh_token` → rotate (old revoked, new pair returned); invalid/expired/revoked refresh → `400 {"error":"invalid_grant"}`. JSON response `token_type=Bearer`, `expires_in`, `scope`. Must answer fast (single DB round trip).
- Background cleanup job removes expired codes/tokens daily.

### MCPal.Server / Portal API (minimal APIs + ASP.NET Identity) and portal (React SPA)
- JSON API under `/api/portal` (cookie auth, `[Authorize]`, anti-forgery header for mutating calls): `POST auth/signup` (company name + admin email/password → Company + PortalUser + sign-in), `POST auth/login`, `POST auth/logout`, `GET auth/me`, `GET keys`, `POST keys` (returns raw key once), `DELETE keys/{id}` (revoke), `GET connections` (live from registry: bridge name, connected since, servers + tool counts, rejected servers), `GET connect-info` (public MCP URL, issuer, Claude Code command). Unauthenticated → 401 JSON (no redirect), SPA routes to login.
- SPA (`server/portal`): React 19 + TypeScript + Vite + React Router; pages Signup, Login, Keys (create → key shown once with copy, revoke), Connections (auto-refresh every 5 s), Connect (instructions: "Add custom connector → OAuth → paste key", header fallback if org has beta, `claude mcp add --transport http mcpal <url>/mcp`), OAuthAuthorize (see OAuth). Typed fetch client, no state library beyond React Query. Styling: plain CSS modules (design system decided later; optional `translogica-design-system` skill if branded).
- Build: `server/portal` `npm run build` → `dist/` copied into `MCPal.Server/wwwroot` by the Server csproj (MSBuild target before publish; Dockerfile has a node build stage). Dev: `vite` on :5173 proxying `/api`, `/oauth`, `/mcp`, `/hub` to :8080. Server serves static files + `MapFallbackToFile("index.html")` for non-API routes.
- Identity cookies only for the portal API; `/mcp` and `/hub/bridge` use `McpBearer` exclusively. Cookie `SameSite=Lax`, `HttpOnly`. Data Protection keys persisted to a volume (`Mcpal:DataProtectionPath`).
- Tests: API integration tests (WebApplicationFactory); SPA gets Vitest unit tests for the API client + authorize page logic; Playwright smoke later (out of MVP).
- No platform-admin UI in MVP; operator uses DB/`dotnet` migration commands.

### MCPal.Bridge
- Config `mcpal.json` (path via `--config`, default next to exe):
  ```json
  {
    "mcpal": { "url": "https://mcpal.example.com", "apiKey": "mcpal_…", "bridgeName": "hq-01" },
    "mcpServers": {
      "kb":   { "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "…" } },
      "wiki": { "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer …" } }
    }
  }
  ```
  `mcpServers` shape = claude_desktop_config / Claude Code format (copy-paste). `MCPAL_API_KEY` env var overrides `mcpal.apiKey`.
- `LocalServerManager`: one `LocalServer` per entry; `McpClient.CreateAsync` with `StdioClientTransport` or `HttpClientTransport` (StreamableHttp, auto-detect SSE); lazy start on first use or at startup (configurable), retry with backoff on failure; subscribes to `tools/list_changed` → refresh catalog → `ToolsChanged`; dead client → dispose + recreate on next call; every failure becomes a `CallToolResponse { IsError }`, never a crash.
- `TunnelClient`: `HubConnectionBuilder.WithUrl(url + "/hub/bridge", o => o.Headers["Authorization"] = "Bearer " + key).WithAutomaticReconnect(backoff 1s..60s)`; on `Connected`/`Reconnected` → `Register(catalog)`; handles `CallTool` by dispatching to `LocalServerManager` (concurrent calls allowed; per-call timeout slightly below the server's); logs `RejectedServers` loudly.
- Hosting: `Host.CreateApplicationBuilder`, `UseWindowsService()` + `UseSystemd()`; CLI verbs `run` (default), `check` (validate config, start local servers, print tool list, exit). Publish single-file self-contained `win-x64` and `linux-x64`.
- Logging: console + rolling file (Microsoft.Extensions.Logging + simple file provider or Serilog).

### Security / tenant isolation rules (enforced + tested)
1. `CompanyId` only ever comes from an authenticated principal (API key or OAuth token), never from URL, body, or hub payload.
2. Registry is keyed by `CompanyId` first; every lookup starts from the caller's company. There is no cross-company enumeration API.
3. Public tool names are resolved through the caller's company map only; a name from another company → "tool not found".
4. Secrets (API keys, codes, tokens) stored hashed; shown once.
5. Tool errors return `ex.Message`-style safe text only; stack traces stay in logs.
6. Access tokens never accepted via query string. Loopback redirect only for registered clients.
7. Revoking a key kills its tunnels and tokens.

### Error handling summary
| Situation | Behaviour |
|---|---|
| Bridge offline | its tools vanish from `tools/list`; call → `IsError` "bridge offline" |
| Duplicate server name in company | second registration rejected for that server, logged, shown in portal |
| Tool call timeout | `IsError` "timed out after N s" |
| Local MCP crash | bridge returns `IsError`, recreates client on next call |
| Invalid/expired key or token | 401 with `WWW-Authenticate` resource metadata |
| Bad refresh token | `400 invalid_grant` |
| Rate limit | 429 |

## Implementation plan (phases; each ends green + committed)

1. ✅ **Scaffold** (done 2026-09-30, uncommitted) — repo, solution, `Directory.Build.props`, projects, `docker-compose.yml`, spec copied to `docs/superpowers/specs/`, README stub. CI-less for now.
2. **Contracts + ToolNaming** — DTOs, hub interface, naming/sanitizing with unit tests.
3. **Storage + Tenancy** — `MCPalDbContext` (Postgres, Identity), migrations, `ApiKeyService` (generate/hash/validate) + tests (Testcontainers).
4. **McpBearer auth + Tunnel** — auth handler (API-key branch), `BridgeHub`, `ConnectionRegistry`, register/reject/disconnect semantics; tests with in-process `HubConnection` against `WebApplicationFactory`.
5. **MCP endpoint + CallRelay** — `MapMcp` stateless with per-tenant handlers, timeout, error mapping, rate limiter; tests with a fake registry entry and a real SDK `McpClient` against the test server; **cross-tenant tests**.
6. **Bridge** — config, `LocalServerManager`, `TunnelClient`, hosting/CLI; unit tests with `MCPal.TestMcpServer` (stdio).
7. **E2E** — server test host + in-process bridge + TestMcpServer + SDK `McpClient` with API key: list, call, error, timeout, bridge restart, two companies isolation.
8. **OAuth AS** — metadata, DCR, authorize context/submit API, token (+refresh rotation), McpBearer token branch, cleanup job; tests scripting the full Claude-like flow through the API (DCR → authorize with key → token → `/mcp` call → refresh → revoke key → 401).
9. **Portal API** — Identity, signup/login/me, keys, connections, connect-info; integration tests for signup + key lifecycle.
10. **React SPA (server/portal)** — Vite scaffold, API client, pages incl. OAuthAuthorize, build integration into Server wwwroot + SPA fallback; Vitest tests.
11. **Packaging + docs** — Dockerfile (node + dotnet multi-stage), compose, bridge single-file publish, README quickstart, `docs/` for tunnel protocol and OAuth.

Execution: after plan approval, commit the spec in the new repo, then follow the superpowers workflow (writing-plans → executing-plans with TDD) per phase.

## Verification (end-to-end)

1. `dotnet test MCPal.slnx` — all unit/integration/E2E green (Testcontainers needs Docker; tests mark inconclusive if absent, like the reference repo).
2. `docker compose up` → server on `http://localhost:8080` serving the built SPA; sign up in portal, create key. (Dev: `npm run dev` in `server/portal` + `dotnet run` in `server/MCPal.Server`.)
3. Run `MCPal.Bridge` with `mcpal.json` pointing `mcpServers` at `MCPal.TestMcpServer` (stdio) → portal `/connections` shows it online with tools.
4. `claude mcp add --transport http mcpal http://localhost:8080/mcp --header "Authorization: Bearer mcpal_…"` → `claude` lists `test__echo` etc. and a tool call returns the echo.
5. OAuth path: `claude mcp add --transport http mcpal-oauth http://localhost:8080/mcp` → browser login page → paste key → tools usable; refresh works after 1 h (shorten via config for test).
6. Real claude.ai: deploy behind TLS (public URL), add custom connector with the URL, OAuth → paste key → tools appear; second company sees nothing of the first.
7. Kill the bridge → tool call returns "bridge offline" error; restart → tools return within one reconnect interval.

## Assumptions / notes for later
- Single server instance; `ConnectionRegistry` in memory. Scale-out later = Redis backplane + routing tool calls to the instance holding the connection (or sticky sessions per company).
- MCP resources/prompts, CIMD support, header-name approval, and the Skill→MCP generator are separate follow-up specs.
- Claude's per-tool-call timeout is not documented; 120 s default is configurable.

## Conventions (Nordstein C#, applied inside the plan layout)

Skills `nordstein-code-basics` and `nordstein-code-csharp` apply. Key points for the next phases:
- TDD: failing test first. `dotnet build` must stay at zero warnings (warnings are errors).
- Never `!`, `null!`, `default!` or `#pragma warning disable` for nullability (only exception: a guard test passing `null!`, marked `// guard test`).
- Guards with BCL helpers (`ArgumentNullException.ThrowIfNull`, …). Timestamps are `DateTimeOffset`; current time only from injected `TimeProvider` (registered as `TimeProvider.System` in both modules; tests use `FakeTimeProvider`).
- Every async method takes a `CancellationToken` and forwards it.
- `internal` by default; public only for interfaces, DTOs (Contracts) and the Autofac modules. `InternalsVisibleTo` is already set: Server → `MCPal.Server.Tests`, `MCPal.E2E.Tests`; Bridge → `MCPal.Bridge.Tests`, `MCPal.E2E.Tests`.
- Constructor injection only, no static state, no service locator. All registrations live in the owning project's module.
- Microsoft libraries first: `IOptions<T>` + `ValidateDataAnnotations().ValidateOnStart()`, `ILogger<T>`, `IMemoryCache`, rate-limiting middleware, `RandomNumberGenerator`/`SHA256`, `PasswordHasher<T>` via Identity, health checks.
- Locking: no `lock` / raw `SemaphoreSlim` in feature code. `ConnectionRegistry` (phase 4) needs a keyed async lock in a shared place, or an immutable-snapshot design, decided there.
- Entities: domain interface + internal immutable record, separate EF entity + config/mapper (five-file pattern) — inside `MCPal.Server` folders `Tenancy/` (domain) and `Storage/` (EF). `UpdatedAt` as concurrency token. No global query filters.
- Tests: names `Subject_Condition_ExpectedOutcome`, no instance fields or `[SetUp]` state, SUT resolved from the container. A base harness that builds a fresh container per test arrives with phase 3.

## Status and handoff

### Done (2026-09-30): phases 1–11
- Server, bridge, contracts, OAuth server, portal API, React SPA, Docker packaging and docs are implemented; see `README.md`, `docs/tunnel-protocol.md`, `docs/oauth.md`.
- Verified: `dotnet build MCPal.slnx` 0 warnings; `dotnet test` green (Bridge 20, Server 123, E2E 7, all with Docker/Testcontainers); `npm test` in `server/portal` 23 passed; `docker compose up --build` serves the SPA, signup → key → bridge → `/mcp` `initialize`, `tools/list`, `tools/call` verified with curl; bridge publishes as a single file for `linux-x64` and `win-x64` (`bridge/packaging/publish-bridge.sh`).

### Deviations from the plan
- Entities are internal mutable classes used by EF directly (`Tenancy/Entities.cs`, `OAuth/Entities.cs`), not the five-file interface/record/generator/config pattern. No global query filters; no `UpdatedAt` concurrency token yet.
- The OAuth challenge and protected-resource metadata are our own minimal implementation, not the SDK's `AddMcp` authentication extension.
- Tunnel calls use `ISingleClientProxy.InvokeAsync` on the untyped `IHubContext<BridgeHub>`; the bridge registers the handler with `On<TRequest, TResult>(name, Func<TRequest, Task<TResult>>)` (a client-result handler cannot take a `CancellationToken`).
- `ConnectionRegistry` is an immutable snapshot updated by compare-and-swap (no locks). The bridge has one `AsyncLock` (the only `SemaphoreSlim` user).
- Logging analyzers CA1848/CA1873 are disabled in `Directory.Build.props` (low-volume logging).
- Bridge has console logging only (no rolling file provider); systemd/journald and the Windows event log cover services.
- Migrations live in `server/MCPal.Server.Storage/Migrations` (moved from `server/MCPal.Server/Storage/Migrations` on 2026-10-04); tests clone a migrated template database per test.
- SPA i18n is a small typed `t()` helper with an English dictionary (`server/portal/src/i18n`).
- Renamed before the first release (2026-09-30): the on-prem process "agent" is now the **bridge** (`MCPal.Bridge`, hub `/hub/bridge`, `ApiKeyPurpose.Bridge`, audit column `BridgeName` via migration `RenameAgentToBridge`, `mcpal-bridge` binary and service), the cloud host is the **server** (`MCPal.Server`, image `mcpal-server`). In `mcpal.json` the section `cloud` is now `mcpal` (`url`, `apiKey`, `bridgeName`). The repo is grouped by artifact (`server/`, `bridge/`, `shared/`, `tests/`) with one `MCPal.slnx` and a solution filter per artifact. Older text in this file may still use the old names.

### Done (2026-09-30): user-level access control and caller context
- Identity is the **portal user**. API keys are `Personal` (personal access tokens, `ApiKey.UserId`) or `Bridge` (company, tunnels only); OAuth tokens and codes belong to the signed-in user, and the OAuth sign-in is the portal login only (migration `UserBoundCredentials`). Users have `DisplayName`, `Disabled`, `ExternalIssuer`/`ExternalSubject` (SSO-ready); disabling or removing a user revokes their tokens, `ActiveUserFilter` re-checks the user on every portal call, the last active owner is protected.
- `Access/`: `AccessGroup` (implicit `Everyone`), `AccessGroupMember`, `AccessGrant`, `AccessPolicyLoader`/`AccessPolicyCache`/`AccessEvaluator`/`AccessService`/`AccessEndpoints` (migration `AccessGroupsAndGrants`, which also creates Everyone `* / *` for existing companies). `MCPal.Contracts.ToolPattern` is the shared glob (moved out of the bridge's `ToolFilter`).
- Audit rows carry `UserId` (migration `AuditUserId`); audit page and export are owner-only.
- `Access/UserContext/`: `SigningKeyStore`, `SigningKeyRotationService`, `UserContextIssuer`, `JwksEndpoints` (migration `SigningKeys`); tunnel protocol **1.2** (`CallToolRequest.User`, gated by `ProtocolVersion.UserContextMinor`); `Mcpal:DataProtectionPath` required in Production.
- Bridge: `UserContextMeta`, `UserTokenHeaderHandler`/`UserTokenScope`, `JwksFileWriter`, config `userContext`, `userTokenHeader`, `jwksFile`; `whoami` in `MCPal.TestMcpServer`; E2E tests with an in-process HTTP local server.
- Docs: `docs/access-control.md` (new), `docs/oauth.md`, `docs/tunnel-protocol.md`, README, CHANGELOG.
- Verified: `dotnet build MCPal.slnx` 0 warnings; `dotnet test` green with Docker (Bridge 116, Server 437, E2E 17, all with Docker/Testcontainers); `npm run typecheck`, `npm test` (107 passed) and `npm run e2e:typecheck` in `server/portal` green. Two tests are timing-sensitive under the load of a parallel `dotnet test MCPal.slnx` and failed once each in a full run while passing alone: `AuditWriterTests.StopAsync_EntriesStillQueued_AreWrittenBeforeShutdownCompletes` (server) and `LocalServerManagerTests.CallToolAsync_CallerCancels_ReturnsErrorQuicklyAndKeepsServerRunning` (bridge, waits for a stdio server to see the cancellation).

#### Deviations from the plan and choices where it was open (user access control)
- Everything in the plan was implemented as written except the points below.
- Key-purpose enum renumbered `Personal = 0`, `Bridge = 1` (CA1008); the portal API speaks `personal` and `bridge`, and sending `allowedServers` is a `400`.
- Disable/enable endpoints are `POST /api/portal/users/{id}/disable` and `/enable`; access views are `GET /api/portal/access/me` and `/access/users/{id}`; groups and grants live under `/api/portal/groups` and `/api/portal/grants`.
- `AuditEntry.authKind` for personal access tokens is `pat` (bridge keys cannot call tools); old rows keep `apikey`.
- The company slug in the caller token comes from `CompanyPolicy` (one more query in the policy loader), so issuing a token needs no database query.
- `McpalEnvironmentValidator` makes `DataProtectionPath` required only when an `IHostEnvironment` is present and Production (tests that build the container directly are not checked).
- `userTokenHeader` validation goes one step beyond the plan: a header that also appears (any name, case-insensitive) in static `headers`, and `userTokenHeader` together with `userContext: false`, are configuration errors.
- Server patterns of grants are validated like server names (not blank, at most 200 characters, no control characters), tool patterns with `ToolPattern.IsValid` (letters, digits, `_ - . * ?`, at most 128 characters, at most 50 per grant).
- The `SigningKeyStore` uses a Postgres advisory transaction lock so several instances never create the same key twice; an unreadable key (lost Data Protection key ring) is skipped and replaced.
- The portal Connections page shows a "passes the caller" badge and the `aud` value per server; older bridges get a warning.

#### Still open (user access control)
- Manual verification with `docker compose up --build`, a real claude.ai or Claude Code connection and the Playwright suite (`npm run e2e`), see the verification steps in the plan.
- Follow-ups named in the plan: bulk invite and domain auto-join, SSO, group ids in tokens, cache invalidation across instances, works council/GDPR hint for the audit log.

### Not done / next
- Real claude.ai verification with a public TLS URL (verification steps 5 and 6 of this plan) still needs a deployed instance.
- Performance suite; platform-admin tooling. (Playwright smoke tests, audit log, password reset, email confirmation and installers are done, see "Post-MVP items".)
- Scale-out (Redis backplane + routing tool calls to the instance owning the tunnel), MCP resources and prompts, CIMD, billing, Skill→MCP generator.

### Post-MVP items (`docs/next-steps.md`)
- Item 1 done: `CancelCall` (protocol 1.1) from server to bridge; `TunnelClient` cancels running calls per request id and on tunnel loss; `LocalServerManager` sends `notifications/cancelled` itself (the SDK client does not when the caller's token fires) and pings before resetting a local server after a timeout. The stateless MCP transport does not link `HttpContext.RequestAborted` to the handler token, so `TenantToolHandlers` links it.
- Item 2 done: `${VAR}` expansion (`Config/EnvironmentExpander.cs`); `CurrentEnvironment()` returns the whole process environment.
- Item 3 done: `includeTools`/`excludeTools` (`Local/ToolFilter.cs`, `ILocalServerManager.HiddenTools`).
- Item 4 done: `/health/live`, `/health/ready` (DbContext check), `/health` alias; compose health check; bridge `statusFile` (`Status/BridgeStatusTracker.cs`) and `status` verb.
- Item 5 done: `OutputSchemaJson`, `StructuredContentJson`, `MetaJson` (protocol 1.1, together with item 1). The bridge strips the SDK-stamped `io.modelcontextprotocol/serverInfo` from `_meta`. An oversize result (>10 MB) closes the tunnel and fails the call at once (tested).
- Item 6 done: `Diagnostics/ServerTelemetry.cs` (`ToolCallOutcome` shared with the audit log), OTLP export via `OTEL_EXPORTER_OTLP_ENDPOINT`, `TraceParent` in `CallToolRequest`, bridge `BridgeTelemetry`. Deviation: Npgsql 9+ emits its own `Npgsql` ActivitySource/Meter, so the `Npgsql.OpenTelemetry` package is not used.
- Item 7 done: `Audit/` (entity `ToolCallAudit`, `AuditWriter` bounded channel, `AuditRetention` + `AuditCleanupService`, `AuditEndpoints`), migration `AddToolCallAudit`, `CallerIdentity` from the principal, OAuth client claim, SPA `AuditPage`. Endpoints live under `/api/portal/audit` (the portal group carries antiforgery and rate limiting), not `/api/audit`. `OAuthCleanupService` now uses `TimeProvider`. Lesson: `services.AddOpenTelemetry()` inside `ServerModule` registers a fallback `IConfiguration` that replaced the host's; `ServerWebServices` removes it.
- Item 8 done: `ApiKeyPurpose` + `AllowedServers` on `ApiKey` (migration `ApiKeyPurposeAndAllowedServers`, existing keys become `Any`), claims `KeyPurpose`/`AllowedServer`, tunnel and `/mcp` policies (403), OAuth authorize refuses bridge keys (`403 key_not_allowed`), tokens inherit restrictions via the token query, `CallerIdentity.Allows`, portal `purpose`/`allowedServers` (default `any` when omitted, SPA default `client`), SPA key form with expiry date, docs updated.
- Item 9a/9b done: `IEmailSender` (`LogEmailSender`, `SmtpEmailSender` via MailKit; chosen by `Mcpal:Smtp:Host`), `AccountMailer`, `AccountEndpoints` (confirm-email, resend-confirmation, forgot-password, reset-password, all 204/generic so nothing enumerates accounts), `RequireConfirmedEmail` + migration `ConfirmExistingUsers`; `PortalRole` (owner/member), `Invitation`, `ApiKey.CreatedByUserId`, migration `UsersRolesAndInvitations`, `TeamService`/`TeamEndpoints` (`/api/portal/users`, `/api/portal/invitations/*`), `OwnerOnlyFilter` reads the role from the database on every call, members create/see/revoke only their own `client` keys, removing a user revokes their `client` keys. `AddDefaultTokenProviders()` was missing for Identity tokens. The SPA and test clients refetch the CSRF token after accepting an invitation (it signs the user in). Race not handled: two owners removing each other at the same moment could leave a company without an owner.
- Item 9c done: Playwright in `server/portal/e2e` (portal signup/key/revoke, wrong password, forgot-password page, OAuth sign-in page with a DCR client), `npm run e2e`, CI job `e2e` against the compose stack. Note: a non-published `dotnet run` in Production serves empty bodies for compressed static assets (`MapStaticAssets` without the publish manifest); run it in Development or use the compose image.
- Item 10 done: `RegisterResult.Code` (`RegisterCodes.UnsupportedProtocol`), `BridgeInfo` seam + 15 min backoff and clear log in `TunnelClient`, `ConnectionInfo.BridgeVersion` shown in the portal with `Mcpal:LatestBridgeVersion` hint (`BridgeVersions.IsOlder`), `bridge/packaging/publish-bridge.sh --version`, `bridge/packaging/package-bridge.sh`, `bridge/packaging/linux` (unit, install/uninstall, `test-install.sh` run in CI) and `bridge/packaging/windows` (install/uninstall.ps1, untested: no PowerShell here), `.github/workflows/release.yml` (tag-triggered; CI reused via `workflow_call`; optional osslsigncode signing; GHCR image). Manual steps still open: test the scripts on a clean Ubuntu and Windows Server VM, and run the release workflow once with `dry_run`.

### Done (2026-10-04): server split into onion rings
- `MCPal.Server` is now six projects: `Domain` (entities, pure rules, ports), `Application` (services, relay, background services), `Storage` (EF Core, migrations), `Infrastructure` (SMTP), `Web` (endpoints, hub, MCP handlers) and the host. Design and decisions: `docs/superpowers/specs/2026-10-04-server-onion-design.md`. No behavior, protocol or schema change; namespaces unchanged.
- New port `IMcpalData` (`Query<T>`, `Add`, `Remove`, `SaveChangesAsync`, `BeginTransactionAsync`; `IDataTransaction.LockAsync` for the Postgres advisory lock). Services and endpoints no longer take `MCPalDbContext`.
- `ArchitectureTests` (`server/MCPal.Server.Tests/Architecture`) fail when a ring references a forbidden assembly, and when the migration is not the first hosted service of the MCPal rings.
- Verified: `dotnet build MCPal.slnx` 0 warnings; Server 445 tests (437 plus 8 architecture and wiring tests), Bridge 116, E2E 17; `docker compose up --build` reaches `/health/ready`, serves the JWKS, answers `/mcp` without a token with 401, and logs no errors.
- `dotnet ef migrations has-pending-model-changes` and `migrations add --project server/MCPal.Server.Storage --output-dir Migrations` work from the new location (checked with dotnet-ef 10.0.12; the probe migration was removed again). All migration files and the model snapshot are byte-identical to before the move.

### Done (2026-10-04): owner setup walkthrough
- Portal page `/setup` (owners) guides the first bridge: download per platform, bridge key created in the page (shown once), pre-filled `mcpal.json`, install commands with the key, live "bridge online" check. Endpoint `GET /api/portal/setup` (`SetupEndpoints`, `OwnerOnlyFilter`).
- Decisions: archives are linked from GitHub releases (`Mcpal:BridgeReleaseBaseUrl` + `Mcpal:LatestBridgeVersion`, fallback to the latest-release page), the server hosts no binaries. The generated `mcpal.json` holds the server URL only: the key travels as `MCPAL_API_KEY`, which the installers already write. The local servers live in a second file, `mcp.json` (Claude Code `.mcp.json` shape, `mcpal.mcpServersFile` to name another), so the owner copies an existing file; MCPal-only per-server settings go into `serverOptions` of `mcpal.json`. The portal has no paste box.
- Landing: login, sign-up and invitation go to `/` (`Home`): owners of a company with no bridge ever connected get `/setup`, everyone else `/keys`. "Connected once" = a bridge key with `LastUsedAt` or an online connection; no migration.
- Not done: the image does not default `Mcpal__LatestBridgeVersion` to its own release, so direct links need that setting. No release tag exists yet.
