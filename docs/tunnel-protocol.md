# Tunnel protocol

The bridge keeps one outbound SignalR connection to `<server>/hub/bridge` (WebSockets, with SSE and long polling as fallbacks, so it works behind corporate proxies). Types live in `shared/MCPal.Contracts`; the JSON hub protocol carries them, and tool schemas and content travel as JSON strings so the contracts stay free of the MCP SDK.

## Authentication

`Authorization: Bearer mcpal_<company>_<secret>` on every request of the connection. Only **bridge keys** (`purpose: bridge`) open tunnels. Personal access tokens (`403`) and OAuth access tokens are rejected (`Tunnel` policy requires `AuthKind=apikey` and `KeyPurpose=Bridge`). The company and the key come from the authenticated principal, never from a message. Access tokens in the query string are ignored.

Revoking the key in the portal closes all tunnels that used it at once. Tunnels whose key expired or whose company was disabled are closed within a minute.

## Bridge → server

| Method | Payload | Result |
|--------|---------|--------|
| `Register` | `BridgeCatalog { BridgeName, BridgeVersion, ProtocolVersion, Servers[] }` (`ToolDescriptor` has `OutputSchemaJson?` since 1.1) | `RegisterResult { Accepted, RejectedServers[], RejectedTools[], Message, Code? }` |

Every `Register` replaces the catalog of the connection. The bridge calls it after connecting, after every automatic reconnect, when a local server reports `tools/list_changed`, and at the 30 s refresh when its tools changed or the last result rejected anything. The retry matters after a silent network drop: the server keeps the old connection (and its server names) until its 60 s client timeout, so the first `Register` of the new connection can be rejected.

Rules enforced by the server:

- `ProtocolVersion` major must equal the server's (`1`); otherwise `Accepted=false` with a message and `Code = "unsupported_protocol"` (1.1). The bridge then logs that it is too old or too new for the server, names where to download the matching bridge, and retries only every 15 minutes instead of every 30 s.
- Server names are unique per company. A server whose name is held by another live connection is rejected (the others are accepted) and shown in the portal.
- Public tool names are `sanitize(server) + "__" + sanitize(tool)` (`[A-Za-z0-9_-]`, at most 64 characters; longer names are truncated and get a 6-character hash suffix). A tool whose public name is already taken (e.g. servers `files.v2` and `files_v2`) is listed in `RejectedTools`; the first registration keeps the name.
- A tool whose `OutputSchemaJson` is not a valid JSON Schema (an object or a boolean) is listed in `RejectedTools`, like one with an invalid input schema.
- A tool whose `InputSchemaJson` is not a JSON Schema object with `"type": "object"`, or whose `AnnotationsJson` are not valid MCP tool annotations, is listed in `RejectedTools`. The server's other tools are accepted.

## Server → bridge

| Method | Payload | Result |
|--------|---------|--------|
| `CallTool` | `CallToolRequest { RequestId, ServerName, ToolName, ArgumentsJson, TraceParent?, User? }` | `CallToolResponse { IsError, ContentJson, ErrorMessage, StructuredContentJson?, MetaJson? }` |
| `CancelCall` (1.1) | `requestId` (string) | none (fire and forget) |

This is a SignalR client result (server invokes the client and awaits the answer). `TraceParent` (1.1) is the W3C `traceparent` of the server's `mcpal.tool_call` span; the bridge starts its `mcpal.local_call` span as a child, so one call is one trace. `ContentJson` is the serialized MCP `content` array. Since 1.1, `StructuredContentJson` carries the result's `structuredContent` and `MetaJson` its `_meta` (without the `serverInfo` entry the SDK stamps on every result, which describes the local server). The server drops either one with a warning when it is not valid JSON; the call still succeeds. All new fields are optional and default to null, so 1.0 bridges and servers interoperate. Calls run concurrently. The bridge limits each call to 110 s (`callTimeoutSeconds`), just below the server timeout (120 s, `Mcpal:ToolCallTimeoutSeconds`).

`CancelCall` tells the bridge that the server stopped waiting for a call: the server timeout expired, the MCP client disconnected, or the call failed. It is best effort. The bridge cancels the running call (and sends `notifications/cancelled` to the local server, which stops the tool) without restarting the local server. Unknown request ids are ignored, because the call may just have finished. The server sends it only to bridges that announced protocol 1.1 or later. When the tunnel is lost, the bridge cancels all running calls, because the server has already failed them.

After a call timeout on the bridge (`callTimeoutSeconds`), the bridge sends a ping to the local server and restarts the server only when the ping fails, so other calls on the same stdio process keep running.

## Caller (1.2)

`CallToolRequest.User` is a `UserContext { Token, UserId, Email?, Name?, Groups[], CompanyId, Company }`: who is calling. `Token` is a short-lived ES256 JWT signed by the MCPal server (verifiable with `GET /.well-known/jwks.json`); the other members repeat its claims. The server sends `User` only to bridges that announced protocol 1.2 or newer (like `CancelCall` for 1.1); older bridges get `null` and pass no caller on.

The bridge hands it to the local MCP server in the `tools/call` request, `params._meta["eu.nordstein.mcp/user"]` = `{ token, sub, email, name, groups, companyId, company }` (stdio and HTTP), and, for HTTP servers with `userTokenHeader` in `mcpal.json`, in that request header (only with tool calls). **Strip rule:** the bridge builds the request itself and never forwards Claude's `_meta`; it also removes the `eu.nordstein.mcp/user` key before it sets its own. A server with `"userContext": false` gets neither. Token format, trust model and verification: [access-control.md](access-control.md). Local servers must fail closed when the caller is missing.

The bridge can keep a copy of the JWKS for local servers without internet access: top-level `jwksFile` in `mcpal.json` (fetched at startup and hourly, replaced atomically).

## Versions

| Version | Change |
|---------|--------|
| 1.0 | `Register`, `CallTool` |
| 1.1 | `CancelCall`; structured content and output schemas; trace propagation |
| 1.2 | `CallToolRequest.User`: the calling user as a signed token plus claims |

Minor versions are additive. The server accepts every 1.x bridge, and a 1.1 bridge works against a 1.0 server (that server never sends `CancelCall`). A 1.2 bridge works against an older server (it never sends `User`, so local servers see no caller). A bridge older than 1.2 works against a 1.2 server, but its local servers get no caller: the portal marks such bridges.

## Failure behaviour

| Situation | Result for Claude |
|-----------|-------------------|
| Bridge offline | tools vanish from `tools/list`; a call returns `isError` "not available" |
| Result larger than the 10 MB message limit | the server closes the tunnel; the call fails at once with `isError` "Tool call failed", the bridge reconnects |
| Tool call timeout | `isError` "timed out after N s" |
| Local server crash | `isError`; the bridge starts a new process on the next call |
| Tunnel lost | bridge reconnects with exponential backoff (1 s … 60 s) and registers again |
