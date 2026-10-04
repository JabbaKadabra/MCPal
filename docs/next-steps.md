# MCPal: next steps

This document describes ten improvements to MCPal after the MVP (phases 1–11 of `plan.md`). Each item is written so that an engineer can pick it up without further context: what the problem is today, the proposed design, the files to change, the tests to write and the acceptance criteria.

> Note: item 8 (API key purposes and server restrictions) was superseded by user-bound credentials and groups: see `docs/access-control.md` and the "user access control" section in `plan.md`. Purposes are now `personal` and `bridge`, and `AllowedServers` no longer exists.

Read `CLAUDE.md`, `plan.md` (sections "Security / tenant isolation rules" and "Status and handoff"), `docs/tunnel-protocol.md` and `docs/oauth.md` before starting.

## Ground rules for every item

- Work test-first (Nordstein conventions, skills `nordstein-code-basics` and `nordstein-code-csharp`). One item per branch and pull request.
- `dotnet build MCPal.slnx` stays at zero warnings. `dotnet test MCPal.slnx` stays green with Docker running. For SPA changes, `npm run typecheck` and `npm test` in `server/portal` stay green.
- Tenant isolation is a hard requirement. `CompanyId` comes only from the authenticated principal. Every new query filters by company explicitly (there are no global query filters). Every new endpoint or hub method needs a cross-tenant test.
- Time only from the injected `TimeProvider`; tests use `FakeTimeProvider`. Every async method takes and forwards a `CancellationToken`.
- No `lock` or raw `SemaphoreSlim` in feature code. Use `Concurrency/AsyncLock` in the bridge, or immutable snapshots with compare-and-swap like `ConnectionRegistry`.
- Register new services only in `ServerModule` / `BridgeModule` (`Module.cs`), or in `ServerWebServices.cs` for ASP.NET pipeline services.
- Package versions only in `Directory.Packages.props`.
- New options go into `McpalOptions` with data annotations (validated on start).
- Tunnel protocol changes: keep them backward compatible where possible (new optional DTO fields at the end of the record, with defaults). Bump `ProtocolVersion.Current` minor for additive changes, major only for breaking changes. Update `docs/tunnel-protocol.md` in the same pull request.
- New EF migrations: `dotnet ef migrations add <Name> --project server/MCPal.Server.Storage --output-dir Migrations`.
- Add a line to `CHANGELOG.md` and update "Status and handoff" in `plan.md` when an item is done.

## Recommended order

| # | Item | Size | Depends on |
|---|------|------|------------|
| 1 | Cancellation propagation to the bridge | M | – |
| 2 | `${VAR}` expansion in `mcpal.json` | S | – |
| 3 | Per-server tool include/exclude lists | S | – |
| 4 | Health and readiness checks | S | – |
| 5 | Structured content and output schema passthrough | M | – |
| 6 | Observability (OpenTelemetry) | M | best after 1 |
| 7 | Tool call audit log | M | best after 6 (shares the outcome classification) |
| 8 | API key purpose and restrictions | L | – |
| 9 | Portal account completion | L | 9b before 8's UI if both are planned |
| 10 | Bridge distribution and releases | M | – |

S = up to 1 day, M = 2–3 days, L = 1 week or more.

Items 1 and 5 both change the tunnel protocol. If both are done close together, ship them as one protocol version `1.1` to avoid two bumps.

---

## 1. Propagate cancellation to the bridge

### Problem

`CallRelay.CallAsync` (`server/MCPal.Server/Mcp/CallRelay.cs`) stops waiting when the server timeout (120 s) expires or when the MCP client disconnects. The bridge does not learn about this. The SignalR client-result handler in `TunnelClient.BuildConnection` (`connection.On<CallToolRequest, CallToolResponse>(...)`) cannot take a `CancellationToken`, so `LocalServerManager.CallToolAsync` keeps running until its own 110 s limit. On that timeout it also resets the whole local server (`ResetQuietlyAsync`), which kills a stdio process that other calls may still use.

Result: abandoned calls hold local resources (DB queries, processes) for up to 110 s, and a user cancel in Claude has no effect on the local server.

### Design

Add a fire-and-forget server-to-bridge message `CancelCall(string requestId)`.

Server side:
- `IBridgeHubClient` (`shared/MCPal.Contracts/HubInterfaces.cs`): add `Task CancelCall(string requestId);`.
- `IBridgeInvoker` / `HubBridgeInvoker` (`server/MCPal.Server/Tunnel/BridgeInvoker.cs`): add `CancelCallAsync(string connectionId, string requestId, CancellationToken)`, implemented with `hub.Clients.Client(connectionId).SendAsync(nameof(IBridgeHubClient.CancelCall), requestId, cancellationToken)`.
- `CallRelay.CallAsync`: in both `catch` branches, and also when the caller's `cancellationToken` is cancelled, send `CancelCall` for `request.RequestId`. Use `CancellationToken.None` with a short timeout (for example 5 s) for this send, because the original token is already cancelled. Swallow and log (warning) failures of the cancel send; it is best effort.
- Only send `CancelCall` to bridges that understand it. Store the bridge's `ProtocolVersion` from `BridgeCatalog` in `ConnectionInfo` (`server/MCPal.Server/Tunnel/ConnectionRegistry.cs`) during `Register`, and expose it on `RegisteredTool` or via a registry lookup. Send only when the minor version is at least 1. (An old SignalR client ignores unknown methods and logs a warning, so this check mainly keeps bridge logs clean.)

Bridge side:
- `TunnelClient`: keep a `ConcurrentDictionary<string, CancellationTokenSource>` of running calls keyed by `RequestId`. `ConcurrentDictionary` is allowed; it is not a lock in feature code.
- In the `CallTool` handler, create a `CancellationTokenSource` linked to `stoppingToken`, add it to the dictionary, call `manager.CallToolAsync(request, cts.Token)`, and remove and dispose it in `finally`.
- Register `connection.On<string>(nameof(IBridgeHubClient.CancelCall), requestId => ...)`, which cancels the matching source if present. Unknown ids are ignored (the call may have just finished).
- When the tunnel is lost (`Reconnecting` event), cancel all running calls: the server has already failed them.
- `LocalServerManager.CallToolAsync`: when `cancellationToken` (not the timeout) is cancelled, do not reset the server. The MCP SDK sends `notifications/cancelled` to the local server when the token passed to `McpClient.CallToolAsync` is cancelled; verify this in a test. Return a `CallToolResponse` with `IsError = true` and "Tool call was cancelled." (the result is discarded by the server, but the method contract says it never throws).
- Reconsider the reset on timeout: resetting a stdio server kills all its other in-flight calls. Keep the reset only when the server does not answer anything anymore (for example, follow the timeout with a quick `ping` and reset only when the ping fails). This is optional; if it is left as is, document the behaviour.

Protocol:
- `ProtocolVersion.Current = "1.1"`. `Major` stays 1.
- `docs/tunnel-protocol.md`: add `CancelCall` to the "Server → bridge" table and describe the best-effort semantics.

### Tests

- `bridge/MCPal.Bridge.Tests`: a `TunnelClient`-level or `LocalServerManager`-level test with `MCPal.TestMcpServer`'s `slow` tool: start `slow(60000)`, cancel the token after 200 ms, assert the call returns within about 1 s with `IsError` and that the next `echo` call on the same server works without a restart (no reset).
- `server/MCPal.Server.Tests/Tunnel`: with `FakeBridge`, make the bridge never answer, advance the timeout and assert that `FakeBridge` received `CancelCall` with the request id. Extend `FakeBridge` to record `CancelCall` messages.
- Same for client disconnect: cancel the MCP request token and assert `CancelCall`.
- Old bridge: a `FakeBridge` registering with protocol `"1.0"` does not receive `CancelCall`.
- `ContractsSerializationTests`: round-trip for the new method payload.
- `tests/MCPal.E2E.Tests`: set `Mcpal:ToolCallTimeoutSeconds` low (for example 2), call `test__slow` with 30000 ms, assert the server returns the timeout error, and assert through the bridge's log or a test hook that the local call was cancelled well before 30 s.

### Acceptance criteria

- A call cancelled by the server stops on the local server within 1 s.
- A cancelled call does not restart the local server.
- A 1.0 bridge still works against the new server, and a 1.1 bridge works against an old server (it receives no `CancelCall`).

---

## 2. `${VAR}` expansion in `mcpal.json`

### Problem

`BridgeConfigLoader.ParseServer` (`bridge/MCPal.Bridge/Config/BridgeConfigLoader.cs`) copies `env`, `headers`, `args` and `url` literally. Tokens for local servers (database passwords, API tokens for internal services) must therefore be written into `mcpal.json` in plain text. Only the MCPal API key has an environment override (`MCPAL_API_KEY`).

### Design

- Support `${NAME}` and `${NAME:-default}` in `command`, `args`, `env` values, `url` and `headers` values. This is the syntax Claude Code uses in `.mcp.json`, so configs stay copy-paste compatible.
- Also support it in `mcpal.url` and `mcpal.apiKey`.
- `$$` escapes a literal `$`. Other `$` characters without `{` stay as they are.
- An unset variable without a default is an error: throw `BridgeConfigException($"Server '{name}': environment variable '{variable}' is not set.")`. Do not silently expand to an empty string.
- Keep `BridgeConfigLoader` pure: `Parse(json, environment, requireMcpal)` already receives the environment as a dictionary. Change `CurrentEnvironment()` to return the full process environment (`Environment.GetEnvironmentVariables()`), not only `MCPAL_API_KEY`.
- Put the expansion in a small internal static class `Config/EnvironmentExpander.cs` with one method `string Expand(string value, IReadOnlyDictionary<string, string?> environment, string context)`. Use a compiled `Regex` (`\$\$|\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}`) via `[GeneratedRegex]`.
- `check` verb: do not print expanded secret values. If it prints server configs, print the unexpanded form.
- Document the syntax in `bridge/MCPal.Bridge/mcpal.example.json` (one example header with `${...}`) and in the README bridge section.

### Tests (`bridge/MCPal.Bridge.Tests/Config/BridgeConfigLoaderTests.cs`)

- Expansion in each field (`command`, `args`, `env`, `url`, `headers`, `mcpal.apiKey`).
- Default value used when unset; default ignored when set.
- `$$` gives `$`.
- Unset variable without default gives `BridgeConfigException` with the server name and variable name in the message.
- A value without `${` is unchanged.
- `MCPAL_API_KEY` still overrides `mcpal.apiKey`.

### Acceptance criteria

A config with `"headers": { "Authorization": "Bearer ${JIRA_TOKEN}" }` works when `JIRA_TOKEN` is set, and `mcpal check` fails with a clear message when it is not.

---

## 3. Per-server tool include/exclude lists

### Problem

The bridge exposes every tool of every local server. Many MCP servers bundle read and write tools (for example, a database server with `query` and `execute`). Admins cannot expose only the safe subset without writing a wrapper server.

### Design

- New optional fields per server in `mcpal.json`:
  ```json
  "postgres": {
    "command": "...",
    "includeTools": ["query", "list_*"],
    "excludeTools": ["drop_*"]
  }
  ```
- Semantics: if `includeTools` is set, a tool must match at least one include pattern. Then any tool matching an exclude pattern is removed. Patterns are case-sensitive globs with `*` and `?` only.
- `LocalServerConfig` (`bridge/MCPal.Bridge/Config/BridgeConfig.cs`): add `IReadOnlyList<string> IncludeTools` and `IReadOnlyList<string> ExcludeTools` (empty lists by default). Parse and validate them in `BridgeConfigLoader` (reject empty or whitespace patterns).
- Put the matching in a small internal class `Local/ToolFilter.cs` (`bool IsExposed(string toolName)`), built once per server from its config. Convert globs to anchored regexes, or use `FileSystemName.MatchesSimpleExpression` from `System.IO.Enumeration` (already in the BCL; check its handling of `*`/`?` fits).
- `LocalServer.ListToolsAsync`: filter the tool list before building the `ServerCatalog`.
- `LocalServerManager.CallToolAsync`: reject calls to a filtered tool with `Failure($"Tool '{name}' is not exposed by this bridge.")` before calling the local server. This check is required: the server only resolves registered tools, but the bridge must not rely on that.
- `check` verb: list hidden tools separately ("hidden by config") so admins can verify their patterns.

### Tests

- `ToolFilter` unit tests: include only, exclude only, both, no lists, `?` and `*`, case sensitivity.
- `LocalServerManagerTests` with `MCPal.TestMcpServer`: `includeTools: ["echo", "add"]` lists exactly those; a call to `fail` returns the "not exposed" error and does not reach the server.
- Config loader tests for parsing and validation.

### Acceptance criteria

Hidden tools do not appear in the portal, in `tools/list` or in `mcpal check` (except in the hidden list), and they cannot be called even with a hand-crafted `CallToolRequest`.

---

## 4. Health and readiness checks

### Problem

`/health` (`services.AddHealthChecks()` in `ServerWebServices.cs`, mapped in `Program.cs`) has no checks. It returns healthy even when PostgreSQL is down, so a load balancer or orchestrator sees a healthy server that cannot authenticate anyone. The bridge has no way to report its state to monitoring except logs.

### Design (server)

- Add package `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` (version in `Directory.Packages.props`).
- `services.AddHealthChecks().AddDbContextCheck<MCPalDbContext>(tags: ["ready"])`.
- Map three endpoints in `Program.cs`:
  - `/health/live`: `Predicate = _ => false` (process is up; no dependency checks).
  - `/health/ready`: `Predicate = check => check.Tags.Contains("ready")`.
  - `/health`: keep as an alias of `/health/ready` for compatibility with existing deployments.
- Keep the response body minimal (status only). Do not expose exception details; health endpoints are anonymous.
- Verify the SPA fallback regex in `Program.cs` does not capture the new paths (mapped endpoints win over the fallback, but add a test).
- `docker-compose.yml` has a health check only for PostgreSQL. Add one for the `server` service on `/health/ready` (the runtime image has no `curl`; use a tiny check in the image or `wget` if available) and document the endpoints in the README deployment section.

### Design (bridge)

- Optional config field `statusFile` (path). When set, the bridge writes a small JSON file on every state change and after every registration:
  ```json
  {
    "updatedAt": "2026-09-30T12:00:00Z",
    "tunnel": "connected",
    "serverUrl": "https://…",
    "lastRegisteredAt": "…",
    "servers": [{ "name": "postgres", "state": "running", "tools": 5 }],
    "rejected": [{ "server": "x", "reason": "…" }]
  }
  ```
  Write to a temp file in the same directory and rename, so readers never see a partial file.
- New CLI verb `status` that reads and prints this file (for admins on the machine). Monitoring tools (Zabbix, Nagios, Windows SCOM) can read the file directly.
- Alternative considered and not recommended for now: a local HTTP status endpoint. It needs a port and firewall decisions on customer machines.

### Tests

- `server/MCPal.Server.Tests/HealthEndpointTests.cs`: `/health/live` returns 200; `/health/ready` returns 200 with the test database; returns 503 when the connection string points to a closed port (use a separate factory configuration).
- `SpaHostingTests`: `/health/ready` is not served by `index.html`.
- Bridge: status file content after connect, after a rejected server and after disconnect (fake time for `updatedAt`).

### Acceptance criteria

Stopping PostgreSQL makes `/health/ready` return 503 within one probe, while `/health/live` stays 200.

---

## 5. Structured content and output schema passthrough

### Problem

MCP tools can declare an `outputSchema` and return `structuredContent` next to `content`. Results can also carry `_meta`. The tunnel drops all of these:
- `ToolDescriptor` (`shared/MCPal.Contracts/Dtos.cs`) has no output schema; `LocalServer.ToDescriptor` does not read `ProtocolTool.OutputSchema`.
- `CallToolResponse` carries only `ContentJson`; `LocalServerManager.CallToolAsync` serializes only `result.Content`.
- `CallRelay.Map` and `ToolListing.TryCreate` therefore cannot pass them on.

Clients that use structured output (and tools whose schema promises it) get less than the local server returned.

### Design

- `ToolDescriptor`: add `string? OutputSchemaJson = null` as the last parameter.
- `CallToolResponse`: add `string? StructuredContentJson = null` and `string? MetaJson = null` as the last parameters.
- Defaults keep old bridges and old servers compatible (System.Text.Json fills missing constructor parameters with their defaults; verify with a serialization test that deserializes the old JSON shape).
- Bridge `LocalServer.ToDescriptor`: serialize `tool.ProtocolTool.OutputSchema` when present.
- Bridge `LocalServerManager.CallToolAsync`: serialize `result.StructuredContent` and `result.Meta` when present. Check the exact property types in `ModelContextProtocol` 2.2.0 (`CallToolResult.StructuredContent`, `Tool.OutputSchema`) before writing the code.
- Server `ToolListing.TryCreate`: parse `OutputSchemaJson` into `Tool.OutputSchema`. An invalid output schema rejects the tool with a clear reason (same pattern as the input schema), so `tools/list` never breaks.
- Server `CallRelay.Map`: set `StructuredContent` and `Meta` on `CallToolResult`. If `StructuredContentJson` is invalid JSON, log a warning and drop it rather than failing the call.
- Size: `MaximumReceiveMessageSize` is 10 MB (`ServerWebServices.cs`). Structured content roughly doubles large results. Keep the limit, but make an oversize response fail cleanly: test what the bridge sees when the SignalR message exceeds the limit and make sure the server returns an `IsError` result, not a hang until timeout.
- Protocol version: `1.1` (combine with item 1 if possible). Update `docs/tunnel-protocol.md`.

### Tests

- `ContractsSerializationTests`: round-trip with the new fields; old JSON without the fields deserializes with `null`.
- `ToolListing` tests: valid output schema is kept; invalid output schema rejects the tool.
- `CallRelay` tests with `FakeBridge`: structured content and meta arrive in the MCP result.
- `MCPal.TestMcpServer`: add a tool `weather` (or `structured`) that returns a typed object, so the SDK generates an output schema and structured content. E2E test: `tools/list` shows the output schema, `tools/call` returns `structuredContent`.

### Acceptance criteria

An SDK `McpClient` connected to `/mcp` sees the same `outputSchema` and `structuredContent` as a client connected directly to the local server.

---

## 6. Observability with OpenTelemetry

### Problem

There are only logs. Operators cannot see tool call rates, latencies, error rates, active tunnels or rate-limit rejections, and cannot trace one call from Claude through the server to the bridge and the local server. This is also a prerequisite for the performance suite and the scale-out decision listed in `plan.md`.

### Design (server)

- Packages: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, and `Npgsql.OpenTelemetry` (Npgsql's own tracing is more reliable than the EF Core instrumentation package).
- Register in `ServerWebServices.cs`: tracing and metrics, with the exporter enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (standard OpenTelemetry environment variables; no new `McpalOptions` fields needed). Service name `mcpal-server`.
- Custom telemetry in a new class `Diagnostics/ServerTelemetry.cs`, registered as a singleton, created from the injected `IMeterFactory` (no static `Meter`, following the "no static state" convention):
  - `ActivitySource("MCPal.Server")`: a span `mcpal.tool_call` in `CallRelay.CallAsync` with tags `mcpal.server`, `mcpal.tool`, `mcpal.outcome`.
  - Counter `mcpal.tool_calls` with tag `outcome` = `ok`, `tool_error`, `timeout`, `offline`, `relay_error`, `cancelled`.
  - Histogram `mcpal.tool_call.duration` (seconds) with the same `outcome` tag.
  - Observable gauge `mcpal.tunnels.active` from `ConnectionRegistry.AllConnections().Count`.
  - Counter `mcpal.bridge.registrations` with tag `result` = `accepted`, `partial`, `rejected`.
  - Counter `mcpal.rate_limit.rejections` with tag `policy`, from `RateLimiterOptions.OnRejected`.
- Company id as a metric tag: do not add it by default (cardinality and privacy). Put it on spans only.
- Extract the outcome classification in `CallRelay` into one method so item 7 (audit log) reuses it.

### Design (bridge)

- Same packages minus ASP.NET Core. Service name `mcpal-bridge`.
- Trace propagation through the tunnel: add `string? TraceParent = null` to `CallToolRequest` (protocol minor bump; combine with items 1 and 5 if possible). The server sets it from `Activity.Current?.Id`. The bridge starts its `mcpal.local_call` span with that parent.
- Exporter off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set, because most customer networks have no collector.

### Tests

- Use `MeterListener` / `ActivityListener` in `server/MCPal.Server.Tests` (or `Microsoft.Extensions.Diagnostics.Testing`'s `MetricCollector<T>`) to assert: a successful call increments `mcpal.tool_calls{outcome=ok}`; a timeout increments `outcome=timeout`; an offline tool increments `outcome=offline`.
- Bridge test: a `CallToolRequest` with a `TraceParent` produces a span with that parent.

### Acceptance criteria

With a local OTLP collector (for example `docker run -p 4317:4317 otel/opentelemetry-collector` or the .NET Aspire dashboard), one tool call shows as one trace spanning server HTTP request, relay and bridge local call, and the counters above appear.

Add an optional `otel` profile to `docker-compose.yml` with the Aspire dashboard for local use.

---

## 7. Tool call audit log

### Problem

Companies that tunnel into internal systems need to know who called which tool and when. Today this exists only in logs, mixed across tenants. Listed in `plan.md` "Not done".

### Design

Entity `ToolCallAudit` in `server/MCPal.Server/Audit/Entities.cs` (feature folder, internal mutable class like the other entities):

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `Guid` | key (use `Guid.CreateVersion7()` for insert locality) |
| `CompanyId` | `Guid` | required, FK to `Company`, indexed with `OccurredAt` |
| `OccurredAt` | `DateTimeOffset` | start of the call, from `TimeProvider` |
| `DurationMs` | `int` | |
| `AuthKind` | `string(16)` | `apikey` or `oauth` |
| `ApiKeyId` | `Guid?` | from the principal |
| `OAuthClientId` | `string(64)?` | needs a new claim, see below |
| `BridgeName` | `string(200)` | from `ConnectionInfo` |
| `ServerName` | `string(200)` | |
| `ToolName` | `string(200)` | local tool name |
| `PublicName` | `string(64)` | |
| `Outcome` | `string(16)` | same values as item 6 |
| `ErrorMessage` | `string(500)?` | truncated safe message, never a stack trace |

Arguments and results are not stored (they may contain personal or confidential data). If a customer asks for it later, add an opt-in per company with its own retention.

Capturing the OAuth client:
- `ValidatedAccessToken` (`server/MCPal.Server/Tenancy/IAccessTokenValidator.cs`) gets `string ClientId`; `OAuthService` already has `OAuthToken.ClientId` in the query.
- New claim `McpalClaims.OAuthClientId`, added in `McpBearerAuthenticationHandler.AuthenticateAccessTokenAsync`.

Writing:
- Do not write synchronously in the request path. Add `Audit/AuditWriter.cs`: a singleton with a bounded `Channel<ToolCallAudit>` (capacity for example 10 000, `FullMode = DropWrite`) and a `BackgroundService` that reads batches (up to 500 entries or 1 s) and inserts them with a scoped `MCPalDbContext`. Count dropped entries and log a warning (and a metric from item 6).
- `CallRelay.CallAsync` builds the entry after the call. Pass the principal (or a small `CallerIdentity` record) from `TenantToolHandlers.Configure` into `CallRelay`, because `CallRelay` currently only receives `companyId`.
- On shutdown, the writer drains the channel with a short deadline.

Retention:
- `McpalOptions.AuditRetentionDays` (`[Range(1, 3650)]`, default 90).
- Extend a cleanup service (follow `OAuth/OAuthCleanupService.cs`) that deletes older rows with `ExecuteDeleteAsync` in batches. Use `TimeProvider` for "now" (the existing `OAuthCleanupService` uses `PeriodicTimer` without `TimeProvider`; fix that in passing by passing the time provider to the timer).

Portal:
- `GET /api/audit?from=&to=&tool=&keyId=&outcome=&cursor=&limit=` in `PortalEndpoints.cs` (or a new `Audit/AuditEndpoints.cs` mapped from `Program.cs`). Company from `CompanyOfAsync`. Keyset pagination on `(OccurredAt desc, Id desc)`; `limit` capped at 200.
- `GET /api/audit/export.csv` with the same filters, streamed, capped (for example 100 000 rows).
- SPA: new page `server/portal/src/pages/AuditPage.tsx` with filters and a table, route and nav entry in `App.tsx` / `Layout.tsx`, strings in `src/i18n/en.ts`, API functions and types in `src/api`.

### Tests

- `AuditWriter`: entries are written in batches; a full channel drops and counts.
- `CallRelay` integration: ok, tool error, timeout and offline calls each produce one row with the right outcome and caller fields (API key and OAuth).
- Cross-tenant: company A's audit endpoint never returns company B's rows, also with a crafted `keyId` of company B.
- Retention: rows older than the retention are deleted, newer ones kept (fake time).
- SPA: `AuditPage.test.tsx` renders rows and applies a filter (use `test/fetchMock.ts`).

### Acceptance criteria

Every tool call through `/mcp` appears in the portal audit page within a few seconds, with the key or OAuth client that made it. The `/mcp` latency does not increase measurably (the write is off the request path).

---

## 8. API key purpose and restrictions

### Problem

One API key does everything: it opens tunnels, works as a bearer token for `/mcp` and logs in through the OAuth authorize page (see `CLAUDE.md`, "One credential type"). A key copied from a bridge's `mcpal.json` on a server gives full user access to all tools, and a user key can open a tunnel and register tools. There is no way to give a team access to only some servers.

### Design

Entity changes (`server/MCPal.Server/Tenancy/Entities.cs`, `ApiKey`):
- `ApiKeyPurpose Purpose` enum: `Any = 0`, `Bridge = 1`, `Client = 2`. Existing keys migrate to `Any`, so nothing breaks.
- `string[] AllowedServers` (PostgreSQL `text[]`, empty = all servers). Only meaningful for `Client` and `Any`.

Enforcement:
- `McpBearerAuthenticationHandler.AuthenticateApiKeyAsync`: add claim `McpalClaims.KeyPurpose` and one `McpalClaims.AllowedServer` claim per allowed server. `ApiKeyService.ValidateAsync` returns these fields in `ValidatedKey`.
- Tunnel policy (`McpBearerDefaults.TunnelPolicy` in `ServerWebServices.cs`): require purpose `Any` or `Bridge`.
- MCP policy (`McpBearerDefaults.McpPolicy`): require purpose `Any` or `Client` for direct API key use.
- OAuth authorize (`OAuthService`, authorize with key): refuse `Bridge` keys with a clear message ("This key can only be used by a bridge."). Tokens issued for a key inherit its restrictions: `ValidatedAccessToken` gets the key's allowed servers (join on `ApiKeyId` in the token query), and the handler adds the same claims.
- Server filter: `TenantToolHandlers.Configure` receives a `ToolScope` (company id plus allowed servers) built from the principal in `ConfigureSessionOptions` (`ServerWebServices.cs`). `ListTools` filters by server. `CallRelay` resolves through `ConnectionRegistry.TryResolve` and then rejects tools of a server outside the scope with the same "not available" message as an unknown tool (do not reveal that the tool exists).
- Revocation and expiry already work through `IsActiveAsync` and `TunnelSweeper`; no change.

Portal:
- `CreateKeyRequest` gets `purpose` and `allowedServers`. Validate server names against the sanitize rules of `ToolNaming` (they may reference servers that are not online yet, so do not require them to exist).
- `ApiKeyResponse` returns both fields.
- `KeysPage.tsx`: purpose selector (default `Client` for new keys, with a short explanation of each option), multi-value input for allowed servers (suggest names from `/api/connections`), expiry date input (the API already accepts `ExpiresAt`, but `KeysPage.tsx` does not offer it yet). Show purpose and restrictions in the key list.
- `ConnectPage.tsx`: tell users to create a `Client` key for Claude and a `Bridge` key for the bridge.
- Changing restrictions of an existing key is out of scope (revoke and create a new one). This keeps OAuth token claims simple.

Docs: update `docs/oauth.md` and README (key types). Update `CLAUDE.md` "One credential type" paragraph.

### Tests

- Tunnel with a `Client` key is refused (401/403 at the hub); with `Bridge` and `Any` accepted.
- `/mcp` with a `Bridge` key is refused; `Client` and `Any` accepted.
- OAuth authorize with a `Bridge` key fails; with a restricted `Client` key, the resulting access token sees only the allowed servers.
- `tools/list` with `AllowedServers = ["a"]` lists only server `a`; calling a tool of server `b` returns "not available".
- Migration test: existing keys have `Any` and no restrictions (template database migration covers it).
- Cross-tenant tests stay green.
- SPA: `KeysPage.test.tsx` covers purpose selection and restrictions display.

### Acceptance criteria

A bridge key leaked from a server cannot be used from Claude, and a client key restricted to `jira` sees only `jira__*` tools, both with direct bearer use and through OAuth.

---

## 9. Portal account completion

### Problem

The portal has signup, login and logout only. There is no password reset (a lost password locks the company out, since each company has one user), no email confirmation, no second user per company and no end-to-end browser test. Listed in `plan.md` "Not done".

Split this into three pull requests.

### 9a. Email, confirmation and password reset

- Abstraction `Portal/IEmailSender.cs` (`Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken)`).
- Implementations: `SmtpEmailSender` (MailKit, options `Mcpal:Smtp:Host/Port/User/Password/From/UseTls`) and `LogEmailSender` (writes the mail to the log; used when SMTP is not configured, for development). Choose by configuration in `ServerModule`.
- Identity: `options.SignIn.RequireConfirmedEmail = true` for new accounts, but do not lock out existing unconfirmed users. Add a migration or startup step that marks existing users confirmed.
- Endpoints (all rate-limited with `PortalEndpoints.RateLimitPolicy`, all anti-forgery protected):
  - `POST /api/auth/confirm-email` (`userId`, `token`).
  - `POST /api/auth/resend-confirmation` (`email`); always returns 204 (no account enumeration).
  - `POST /api/auth/forgot-password` (`email`); always 204.
  - `POST /api/auth/reset-password` (`email`, `token`, `newPassword`).
- Links in mails use `McpalOptions.PublicUrl` and SPA routes `/confirm-email` and `/reset-password`.
- SPA pages: `ForgotPasswordPage`, `ResetPasswordPage`, `ConfirmEmailPage`; link from `LoginPage`.
- Tests: token flow end to end with a capturing fake `IEmailSender` registered in `ServerWebApplicationFactory`; no enumeration (same response for known and unknown emails); rate limit.

### 9b. Several users per company, roles and invitations

- `PortalUser.Role`: `Owner` or `Member` (enum stored as string). The signup user becomes `Owner`.
- Entity `Invitation` (`Id`, `CompanyId`, `Email`, `Role`, `TokenHash`, `ExpiresAt`, `AcceptedAt`, `CreatedByUserId`). Token hashed like API keys.
- Endpoints: `GET/POST /api/users` (list, invite; owner only), `DELETE /api/users/{id}` (owner only; the last owner cannot be removed), `POST /api/invitations/accept` (anonymous with token: sets password, creates user in the inviting company).
- Authorization: an `Owner` policy for key management and user management; `Member` can view connections, audit and connect info, and create `Client` keys for themselves (if item 8 is done).
- Company always from the signed-in user (`CompanyOfAsync`), never from the request. Cross-tenant tests for every endpoint, including accepting an invitation token of company B while signed in to company A.
- SPA: `UsersPage.tsx`, invitation accept page.

### 9c. Playwright smoke tests

- New folder `server/portal/e2e` with Playwright (`@playwright/test` as a dev dependency).
- Target: `docker compose up --build` stack, or `dotnet run` plus `npm run build` in CI.
- Scenarios: signup → create key → key shown once → revoke; OAuth authorize page with a DCR client created through the API → paste key → redirect with code; login failure message.
- CI: new job in `.github/workflows/ci.yml` that starts the compose stack, waits for `/health/ready` (item 4) and runs Playwright headless; upload traces on failure.

### Acceptance criteria

A user can reset a forgotten password through email, an owner can invite a colleague who then signs in to the same company, and the Playwright suite runs green in CI.

---

## 10. Bridge distribution and releases

### Problem

`bridge/packaging/publish-bridge.sh` builds single files locally. There is no release pipeline, no versioning, no install script for Windows services or systemd, and no signed binaries. Customers must set up the service by hand. `plan.md` lists installers under "Not done".

### Design

Versioning:
- Git tag `vX.Y.Z` is the source of truth. Pass `-p:Version=X.Y.Z` to `dotnet publish`. `TunnelClient.BridgeVersion` already reads the assembly version and sends it in `BridgeCatalog`.
- Show the bridge version in the portal connections page: add `BridgeVersion` to `ConnectionInfo` (set in `ConnectionRegistry.Register` from the catalog) and to `ConnectionResponse` in `PortalEndpoints.cs`, and render it in `ConnectionsPage.tsx`.

Release workflow `.github/workflows/release.yml`, triggered on `v*` tags:
- Matrix `linux-x64`, `linux-arm64`, `win-x64`.
- Build and test first (reuse the CI steps or call the CI workflow).
- Publish each RID single-file self-contained, as in `publish-bridge.sh`.
- Package: `mcpal-bridge-<version>-<rid>.tar.gz` (Linux) or `.zip` (Windows) containing the binary, `mcpal.example.json`, the install script and a short `README.txt`.
- `sha256sums.txt` for all archives.
- Create a GitHub release with `gh release create` and attach the files.
- Windows code signing: optional step (Azure Trusted Signing or a code signing certificate as a repository secret). Unsigned binaries trigger SmartScreen warnings; decide with the business side whether to buy a certificate. Leave the step behind an `if: secrets.… != ''` guard.
- Server image: build and push the Docker image to GHCR with the same version tag.

Install scripts (new folder `bridge/packaging/`):
- `bridge/packaging/linux/mcpal-bridge.service`: systemd unit with `Type=notify` (the bridge uses `UseSystemd()`), `User=mcpal`, `WorkingDirectory=/etc/mcpal`, `EnvironmentFile=-/etc/mcpal/bridge.env` (for `MCPAL_API_KEY` and `${VAR}` secrets from item 2), `Restart=always`, and hardening (`NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `ReadWritePaths=/var/lib/mcpal`). Note: `ProtectSystem` and `ProtectHome` can break local stdio servers that need other paths; document how to relax them.
- `bridge/packaging/linux/install.sh`: creates the user, copies the binary to `/opt/mcpal`, the config to `/etc/mcpal/mcpal.json` (does not overwrite an existing one), the env file with mode 600, installs and enables the unit, and runs `mcpal-bridge check` before starting.
- `bridge/packaging/windows/install.ps1`: copies to `C:\Program Files\MCPal`, config to `C:\ProgramData\MCPal\mcpal.json` (restricted ACL: Administrators and the service account only, because it may contain secrets), creates the service with `New-Service` (automatic start, recovery actions via `sc.exe failure`), runs `check`, starts the service. `uninstall.ps1` counterpart.
- An MSI (WiX) can come later if customers require it for software distribution tools.

Version mismatch message:
- When `RegisterResult.Accepted` is false because of an unsupported protocol version, the bridge currently logs the server's message at error level (`TunnelClient.LogResult`). Make this explicit: detect the protocol rejection (add `string? Code = null` to `RegisterResult`, for example `"unsupported_protocol"`), log "This bridge (version X, protocol Y) is too old/new for the server; download the current bridge from <PublicUrl>" and stop reconnecting with a long backoff instead of retrying every 30 s.
- Show a warning in the portal for bridges that are older than the latest release (optional; needs the latest version as a config value `Mcpal:LatestBridgeVersion`).

Docs: README install sections for Linux and Windows using the release archives and scripts.

### Tests

- Release workflow: run it on a test tag in a fork or with `workflow_dispatch` and a dry-run input that skips `gh release create`.
- `ConnectionsPage.test.tsx` and `PortalApiTests`: bridge version is shown.
- Bridge test: protocol rejection with code `unsupported_protocol` produces the clear message and the long backoff.
- Install scripts: manual test on a clean Ubuntu VM and a clean Windows Server VM; record the steps in the pull request.

### Acceptance criteria

Pushing a `v1.1.0` tag produces a GitHub release with signed or unsigned archives for three platforms and checksums, and an admin can install the bridge as a service on Linux or Windows with one script call.

---

## Out of scope for now

- Scale-out with a Redis backplane and routing calls to the instance that owns the tunnel. Wait for the metrics from item 6 and the performance suite.
- MCP resources and prompts, CIMD, the Skill→MCP generator: separate specs according to `plan.md`.
- `tools/list_changed` notifications to Claude: not possible with the stateless MCP transport (`ListChanged = false` in `TenantToolHandlers`). Revisit only if the transport becomes stateful.
- Billing and platform-admin tooling.
