# Changelog

## [Unreleased]

### Added
- Release pipeline for tags `vX.Y.Z`: agent archives for Linux (x64, arm64) and Windows (x64) with install scripts, checksums, a GitHub release and the cloud image on GHCR. Install scripts set up the agent as a systemd or Windows service.
- The Connections page shows the agent version and, with `Mcpal:LatestAgentVersion`, a hint for agents that are older.
- An agent that is too old or too new for the cloud says so, points to the download and backs off to 15-minute checks instead of retrying every 30 seconds.
- Playwright smoke tests for the portal and the Claude sign-in page, run in CI against the compose stack.
- Portal accounts: email confirmation and password reset by mail (`Mcpal:Smtp:*`, or logged when no SMTP host is set), several users per company with owner and member roles, and invitations by email. Accounts from before are confirmed and owners.
- API keys have a purpose: agent keys only open tunnels, Claude keys only work on `/mcp` and for the Claude sign-in. Claude keys can be limited to some servers and can get an expiry date in the portal. Existing keys keep both purposes and all servers.
- Audit log of tool calls per company (caller key or OAuth client, agent, tool, outcome, duration) with a portal page, filters and CSV export; rows are kept for `Mcpal:AuditRetentionDays` (90) days.
- OpenTelemetry traces and metrics for cloud and agent (tool calls by outcome, durations, active tunnels, registrations, rate-limit rejections), exported through `OTEL_EXPORTER_OTLP_ENDPOINT`. Trace context travels through the tunnel. `docker compose --profile otel` starts a local dashboard.
- Tools with an `outputSchema` keep it, and results keep `structuredContent` and `_meta`, when relayed through MCPal (tunnel protocol 1.1).
- Health endpoints `/health/live` and `/health/ready`; `/health` (and the compose health check) now fails with 503 when PostgreSQL is down.
- Agent `statusFile` and `status` verb: monitoring tools can read the tunnel state, servers and rejections from a JSON file.
- `includeTools` / `excludeTools` per local server in `mcpal.json` (glob patterns) to expose only a subset of a server's tools. `mcpal check` lists hidden tools.
- `${VAR}` and `${VAR:-default}` in `mcpal.json` (`command`, `args`, `env`, `url`, `headers`, `cloud.url`, `cloud.apiKey`), so secrets for local servers can come from the environment. An unset variable without default is a configuration error.
- MVP: cloud with a public MCP endpoint, per-company tool relay, agent tunnel (SignalR), OAuth 2.1 sign-in for Claude, API keys, web portal, and the agent for Windows and Linux.

### Changed
- New portal accounts must confirm their email address before a password login works. Signup still signs in immediately.

### Fixed
- A tool call that Claude cancels, that times out in the cloud or that loses its tunnel is now cancelled on the agent and in the local MCP server, instead of running until the agent timeout. A timed-out call no longer restarts a local server that still answers.
- An agent whose servers were rejected (for example right after a network drop, while the cloud still held its old connection) now registers again every 30 s instead of staying without tools until restart.
- Tunnels close when their API key expires or the company is disabled, and a key revoked while an agent connects no longer leaves the tunnel open.
- `/mcp` rate limits count per API key or company; requests with invalid tokens share a per-IP limit instead of bypassing it.
- A failed signup (for example a too short password) no longer leaves a company behind that blocks the company's URL name.
- The key column in the portal shows six secret characters, so keys of one company can be told apart.
- `MCPal.Agent --config <path>` without a verb runs the agent instead of failing with "Unknown verb".
- One agent tool with an invalid schema or annotations no longer empties `tools/list` for the whole company; the tool is skipped and shown in the portal.
- Tools whose public names collide (for example servers `files.v2` and `files_v2`) are shown in the portal with the reason instead of being dropped silently.

### Added
- `Mcpal:TrustedProxyNetworks` setting: client address and HTTPS scheme are taken from `X-Forwarded-*` headers of these proxies only.
