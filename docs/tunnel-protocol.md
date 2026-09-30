# Tunnel protocol

The agent keeps one outbound SignalR connection to `<cloud>/hub/agent` (WebSockets, with SSE and long polling as fallbacks, so it works behind corporate proxies). Types live in `src/MCPal.Contracts`; the JSON hub protocol carries them, and tool schemas and content travel as JSON strings so the contracts stay free of the MCP SDK.

## Authentication

`Authorization: Bearer mcpal_<company>_<secret>` on every request of the connection. Only API keys open tunnels; OAuth access tokens are rejected (`Tunnel` policy requires `AuthKind=apikey`). The company and the key come from the authenticated principal, never from a message. Access tokens in the query string are ignored.

Revoking the key in the portal closes all tunnels that used it.

## Agent → cloud

| Method | Payload | Result |
|--------|---------|--------|
| `Register` | `AgentCatalog { AgentName, AgentVersion, ProtocolVersion, Servers[] }` | `RegisterResult { Accepted, RejectedServers[], Message }` |
| `ToolsChanged` | `AgentCatalog` (same shape, replaces the previous catalog) | – |

The agent calls `Register` after connecting and after every automatic reconnect, and `ToolsChanged` when a local server reports `tools/list_changed` or its tools differ at the 30 s refresh.

Rules enforced by the cloud:

- `ProtocolVersion` major must equal the cloud's (`1`); otherwise `Accepted=false` with a message.
- Server names are unique per company. A server whose name is held by another live connection is rejected (the others are accepted) and shown in the portal.
- Public tool names are `sanitize(server) + "__" + sanitize(tool)` (`[A-Za-z0-9_-]`, at most 64 characters; longer names are truncated and get a 6-character hash suffix).

## Cloud → agent

| Method | Payload | Result |
|--------|---------|--------|
| `CallTool` | `CallToolRequest { RequestId, ServerName, ToolName, ArgumentsJson }` | `CallToolResponse { IsError, ContentJson, ErrorMessage }` |

This is a SignalR client result (server invokes the client and awaits the answer). `ContentJson` is the serialized MCP `content` array. Calls run concurrently. The agent limits each call to 110 s (`callTimeoutSeconds`), just below the cloud timeout (120 s, `Mcpal:ToolCallTimeoutSeconds`).

## Failure behaviour

| Situation | Result for Claude |
|-----------|-------------------|
| Agent offline | tools vanish from `tools/list`; a call returns `isError` "not available" |
| Tool call timeout | `isError` "timed out after N s" |
| Local server crash | `isError`; the agent starts a new process on the next call |
| Tunnel lost | agent reconnects with exponential backoff (1 s … 60 s) and registers again |
