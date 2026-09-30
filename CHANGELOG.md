# Changelog

## [Unreleased]

### Added
- MVP: cloud with a public MCP endpoint, per-company tool relay, agent tunnel (SignalR), OAuth 2.1 sign-in for Claude, API keys, web portal, and the agent for Windows and Linux.

### Fixed
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
