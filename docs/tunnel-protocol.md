# Tunnel protocol

The agent keeps one outbound SignalR connection to `<cloud>/hub/agent` (WebSockets, with SSE and long polling as fallbacks, so it works behind corporate proxies). Types live in `src/MCPal.Contracts`; the JSON hub protocol carries them, and tool schemas and content travel as JSON strings so the contracts stay free of the MCP SDK.

## Authentication

`Authorization: Bearer mcpal_<company>_<secret>` on every request of the connection. Only API keys open tunnels; OAuth access tokens are rejected (`Tunnel` policy requires `AuthKind=apikey`). The company and the key come from the authenticated principal, never from a message. Access tokens in the query string are ignored.

Revoking the key in the portal closes all tunnels that used it at once. Tunnels whose key expired or whose company was disabled are closed within a minute.

## Agent → cloud

| Method | Payload | Result |
|--------|---------|--------|
| `Register` | `AgentCatalog { AgentName, AgentVersion, ProtocolVersion, Servers[] }` (`ToolDescriptor` has `OutputSchemaJson?` since 1.1) | `RegisterResult { Accepted, RejectedServers[], RejectedTools[], Message, Code? }` |

Every `Register` replaces the catalog of the connection. The agent calls it after connecting, after every automatic reconnect, when a local server reports `tools/list_changed`, and at the 30 s refresh when its tools changed or the last result rejected anything. The retry matters after a silent network drop: the cloud keeps the old connection (and its server names) until its 60 s client timeout, so the first `Register` of the new connection can be rejected.

Rules enforced by the cloud:

- `ProtocolVersion` major must equal the cloud's (`1`); otherwise `Accepted=false` with a message and `Code = "unsupported_protocol"` (1.1). The agent then logs that it is too old or too new for the cloud, names where to download the matching agent, and retries only every 15 minutes instead of every 30 s.
- Server names are unique per company. A server whose name is held by another live connection is rejected (the others are accepted) and shown in the portal.
- Public tool names are `sanitize(server) + "__" + sanitize(tool)` (`[A-Za-z0-9_-]`, at most 64 characters; longer names are truncated and get a 6-character hash suffix). A tool whose public name is already taken (e.g. servers `files.v2` and `files_v2`) is listed in `RejectedTools`; the first registration keeps the name.
- A tool whose `OutputSchemaJson` is not a valid JSON Schema (an object or a boolean) is listed in `RejectedTools`, like one with an invalid input schema.
- A tool whose `InputSchemaJson` is not a JSON Schema object with `"type": "object"`, or whose `AnnotationsJson` are not valid MCP tool annotations, is listed in `RejectedTools`. The server's other tools are accepted.

## Cloud → agent

| Method | Payload | Result |
|--------|---------|--------|
| `CallTool` | `CallToolRequest { RequestId, ServerName, ToolName, ArgumentsJson, TraceParent? }` | `CallToolResponse { IsError, ContentJson, ErrorMessage, StructuredContentJson?, MetaJson? }` |
| `CancelCall` (1.1) | `requestId` (string) | none (fire and forget) |

This is a SignalR client result (server invokes the client and awaits the answer). `TraceParent` (1.1) is the W3C `traceparent` of the cloud's `mcpal.tool_call` span; the agent starts its `mcpal.local_call` span as a child, so one call is one trace. `ContentJson` is the serialized MCP `content` array. Since 1.1, `StructuredContentJson` carries the result's `structuredContent` and `MetaJson` its `_meta` (without the `serverInfo` entry the SDK stamps on every result, which describes the local server). The cloud drops either one with a warning when it is not valid JSON; the call still succeeds. All new fields are optional and default to null, so 1.0 agents and clouds interoperate. Calls run concurrently. The agent limits each call to 110 s (`callTimeoutSeconds`), just below the cloud timeout (120 s, `Mcpal:ToolCallTimeoutSeconds`).

`CancelCall` tells the agent that the cloud stopped waiting for a call: the cloud timeout expired, the MCP client disconnected, or the call failed. It is best effort. The agent cancels the running call (and sends `notifications/cancelled` to the local server, which stops the tool) without restarting the local server. Unknown request ids are ignored, because the call may just have finished. The cloud sends it only to agents that announced protocol 1.1 or later. When the tunnel is lost, the agent cancels all running calls, because the cloud has already failed them.

After a call timeout on the agent (`callTimeoutSeconds`), the agent sends a ping to the local server and restarts the server only when the ping fails, so other calls on the same stdio process keep running.

## Versions

| Version | Change |
|---------|--------|
| 1.0 | `Register`, `CallTool` |
| 1.1 | `CancelCall`; structured content and output schemas; trace propagation |

Minor versions are additive. The cloud accepts every 1.x agent, and a 1.1 agent works against a 1.0 cloud (that cloud never sends `CancelCall`).

## Failure behaviour

| Situation | Result for Claude |
|-----------|-------------------|
| Agent offline | tools vanish from `tools/list`; a call returns `isError` "not available" |
| Result larger than the 10 MB message limit | the cloud closes the tunnel; the call fails at once with `isError` "Tool call failed", the agent reconnects |
| Tool call timeout | `isError` "timed out after N s" |
| Local server crash | `isError`; the agent starts a new process on the next call |
| Tunnel lost | agent reconnects with exponential backoff (1 s … 60 s) and registers again |
