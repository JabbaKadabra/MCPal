# Changelog

## [Unreleased]

### Breaking (upgrade notes)
- **Users, not keys, are the identity.** API keys are now **personal access tokens** (act as their user, work on `/mcp`) or **bridge keys** (company, tunnels only). Migration `UserBoundCredentials`: former `bridge` keys stay bridge keys; former `any` keys become **bridge keys** (so running tunnels keep working; they no longer work as bearer tokens on `/mcp`); former `client` keys become personal access tokens of the user who created them, and `client` keys without a creator are deleted. Per-key server lists (`AllowedServers`) are gone; use groups. Everyone who used a bearer key on `/mcp` needs a personal access token.
- **OAuth sign-in is the portal login.** The sign-in page no longer accepts pasted API keys, and all OAuth tokens and codes were deleted: every Claude connection authorizes again. Claude users need an MCPal account (owners invite them).
- **Bridges older than protocol 1.2** keep working but pass no caller to local servers; local servers that rely on the caller must refuse calls without one.
- `Mcpal:DataProtectionPath` is now required in Production (it protects the signing keys of the caller tokens).
- The audit log page and export are for owners only.

### Changed
- After signing in, owners of a company that never had a bridge connected land on the new **Setup** walkthrough (`/setup`) instead of the API keys page. Everyone else, and owners once a bridge has connected, land on the API keys page as before.
- Internal: the server is split into onion rings (`MCPal.Server.Domain`, `.Application`, `.Storage`, `.Infrastructure`, `.Web` and the host `MCPal.Server`). No change to endpoints, protocol or database schema. EF migrations now live in `server/MCPal.Server.Storage/Migrations`.

### Added
- **Local servers in their own file.** The bridge reads the local MCP servers from `mcp.json` next to `mcpal.json` (or from `mcpal.mcpServersFile`), in the format of Claude Code's `.mcp.json`: copy an existing file as it is. An inline `mcpServers` block in `mcpal.json` keeps working; a server name in both places is an error. MCPal-only per-server settings (`includeTools`, `excludeTools`, `userContext`, `userTokenHeader`) can be set in the new `serverOptions` block of `mcpal.json` (they win over the same fields inside a server entry). The install scripts create `mcp.json` from `mcp.example.json` on a first install and never overwrite it; the release archives contain `mcp.example.json`, and `mcpal.example.json` no longer contains servers.
- **Setup walkthrough for owners** (portal page `/setup`, endpoint `GET /api/portal/setup`, owners only): download link per platform, a bridge key created on the spot, a pre-filled `mcpal.json` (server URL, no secret) and the instruction to put the owner's existing Claude Code `.mcp.json` next to it as `mcp.json`, the install commands with the key, and a live check that the bridge connected. New option `Mcpal__BridgeReleaseBaseUrl` (default: the GitHub releases of this repository, layout `<url>/download/v<version>/mcpal-bridge-<version>-<rid>`); direct archive links need `Mcpal__LatestBridgeVersion`, otherwise the page links to the latest release.
- **Access control**: groups with grants (server glob plus tool globs), evaluated live per request. The built-in *Everyone* group (granted `* / *` for new and existing companies) keeps everything working until an owner narrows it; owners may use every tool. Forbidden tools are missing from `tools/list` and answer like unknown tools. Portal pages **Groups** (owners) and **My access**; users can be disabled and enabled, and the Users page shows groups and effective access.
- **Caller identity for local MCP servers**: every tool call carries the user as a short-lived ES256 token (verifiable with `/.well-known/jwks.json`, keys rotate automatically) plus plain claims, in `_meta["eu.nordstein.mcp/user"]` and optionally in an HTTP header (`userTokenHeader`). Tunnel protocol 1.2. Bridge settings `userContext`, `userTokenHeader` and `jwksFile`. See `docs/access-control.md`.
- The audit log records the user of each call and can be filtered and exported by user.
- Display name for users (`PATCH /api/portal/auth/me`).
- Release pipeline for tags `vX.Y.Z`: bridge archives for Linux (x64, arm64) and Windows (x64) with install scripts, checksums, a GitHub release and the server image on GHCR. Install scripts set up the bridge as a systemd or Windows service.
- The Connections page shows the bridge version and, with `Mcpal:LatestBridgeVersion`, a hint for bridges that are older.
- A bridge that is too old or too new for the server says so, points to the download and backs off to 15-minute checks instead of retrying every 30 seconds.
- Playwright smoke tests for the portal and the Claude sign-in page, run in CI against the compose stack.
- Portal accounts: email confirmation and password reset by mail (`Mcpal:Smtp:*`, or logged when no SMTP host is set), several users per company with owner and member roles, and invitations by email. Accounts from before are confirmed and owners.
- API keys have a purpose: bridge keys only open tunnels, Claude keys only work on `/mcp` and for the Claude sign-in. Claude keys can be limited to some servers and can get an expiry date in the portal. Existing keys keep both purposes and all servers.
- Audit log of tool calls per company (caller key or OAuth client, bridge, tool, outcome, duration) with a portal page, filters and CSV export; rows are kept for `Mcpal:AuditRetentionDays` (90) days.
- OpenTelemetry traces and metrics for server and bridge (tool calls by outcome, durations, active tunnels, registrations, rate-limit rejections), exported through `OTEL_EXPORTER_OTLP_ENDPOINT`. Trace context travels through the tunnel. `docker compose --profile otel` starts a local dashboard.
- Tools with an `outputSchema` keep it, and results keep `structuredContent` and `_meta`, when relayed through MCPal (tunnel protocol 1.1).
- Health endpoints `/health/live` and `/health/ready`; `/health` (and the compose health check) now fails with 503 when PostgreSQL is down.
- Bridge `statusFile` and `status` verb: monitoring tools can read the tunnel state, servers and rejections from a JSON file.
- `includeTools` / `excludeTools` per local server in `mcpal.json` (glob patterns) to expose only a subset of a server's tools. `mcpal check` lists hidden tools.
- `${VAR}` and `${VAR:-default}` in `mcpal.json` (`command`, `args`, `env`, `url`, `headers`, `mcpal.url`, `mcpal.apiKey`), so secrets for local servers can come from the environment. An unset variable without default is a configuration error.
- MVP: server with a public MCP endpoint, per-company tool relay, bridge tunnel (SignalR), OAuth 2.1 sign-in for Claude, API keys, web portal, and the bridge for Windows and Linux.

### Changed
- The on-prem agent is now the **bridge** and the cloud is the **server**. Binary, service and install names are `mcpal-bridge`, the tunnel hub is `/hub/bridge`, the image is `mcpal-server`, and "Agent" keys are "Bridge" keys. In `mcpal.json` the `cloud` section is now `mcpal`, with `bridgeName` instead of `agentName`. The status file reports `serverUrl`.
- New portal accounts must confirm their email address before a password login works. Signup still signs in immediately.

### Fixed
- A tool call that Claude cancels, that times out on the server or that loses its tunnel is now cancelled on the bridge and in the local MCP server, instead of running until the bridge timeout. A timed-out call no longer restarts a local server that still answers.
- A bridge whose servers were rejected (for example right after a network drop, while the server still held its old connection) now registers again every 30 s instead of staying without tools until restart.
- Tunnels close when their API key expires or the company is disabled, and a key revoked while a bridge connects no longer leaves the tunnel open.
- `/mcp` rate limits count per API key or company; requests with invalid tokens share a per-IP limit instead of bypassing it.
- A failed signup (for example a too short password) no longer leaves a company behind that blocks the company's URL name.
- The key column in the portal shows six secret characters, so keys of one company can be told apart.
- `MCPal.Bridge --config <path>` without a verb runs the bridge instead of failing with "Unknown verb".
- One bridge tool with an invalid schema or annotations no longer empties `tools/list` for the whole company; the tool is skipped and shown in the portal.
- Tools whose public names collide (for example servers `files.v2` and `files_v2`) are shown in the portal with the reason instead of being dropped silently.

### Added
- `Mcpal:TrustedProxyNetworks` setting: client address and HTTPS scheme are taken from `X-Forwarded-*` headers of these proxies only.
