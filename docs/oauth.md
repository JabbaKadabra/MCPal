# Authentication and OAuth

MCPal has one credential type, the **API key** (`mcpal_<8 char company id>_<40 random characters>`). It is stored as a SHA-256 hash, shown once, can expire, and can be revoked. Claude can use it in two ways:

1. **Bearer**: `Authorization: Bearer mcpal_…` (Claude Code `--header`, or organizations that can set static headers on custom connectors).
2. **OAuth 2.1**: the built-in authorization server, used by the claude.ai custom connector flow.

## Discovery

A request to `/mcp` without valid credentials gets `401` with
`WWW-Authenticate: Bearer resource_metadata="<PublicUrl>/.well-known/oauth-protected-resource/mcp"`.

| Endpoint | Purpose |
|----------|---------|
| `GET /.well-known/oauth-protected-resource[/mcp]` (RFC 9728) | resource `<PublicUrl>/mcp`, authorization server, scope `mcp` |
| `GET /.well-known/oauth-authorization-server` (RFC 8414) | endpoints, `S256` only, public clients (`none`) |
| `POST /oauth/register` (RFC 7591) | dynamic client registration; redirect URIs limited to `https://claude.ai/api/mcp/auth_callback` and loopback (`http://localhost` / `http://127.0.0.1`, any port) |
| `GET /oauth/authorize` | the React sign-in page |
| `GET /api/oauth/authorize/context`, `POST /api/oauth/authorize` | JSON API of that page; validates client, redirect URI, PKCE (`S256` required) and `resource` |
| `POST /oauth/token` | form-urlencoded only; `authorization_code` (PKCE) and `refresh_token` (rotation) |

## Sign-in

## Key purpose and server restrictions

Every key has a purpose, chosen at creation and never changed (revoke and create a new key instead, which keeps the claims of issued tokens simple):

| Purpose | Tunnel (`/hub/agent`) | `/mcp` bearer | OAuth sign-in |
|---------|----------------------|---------------|---------------|
| `agent` | yes | 403 | refused: `403 key_not_allowed` "This key can only be used by an agent." |
| `client` | 403 | yes | yes |
| `any` | yes | yes | yes |

Keys created before purposes existed are `any`. A key for Claude (`client` or `any`) can also carry a list of allowed servers (empty = all). Tools of other servers are missing from `tools/list`, and calling one answers exactly like an unknown tool ("not available"), so its existence is not revealed. Server names are compared without case and can be listed before the server is online. Agent keys cannot be restricted, because they open tunnels rather than list tools.

Tokens issued from a key inherit its purpose and allowed servers (the token lookup reads them from the key on every request). A token issued from a portal session has no key and no restrictions.

## Sign-in page

The sign-in page asks the user to paste an API key. Users who are signed in to the portal can instead press **Connect as <company>** (anti-forgery protected). Either way the authorization code is bound to the company; a pasted key also binds the tokens to that key.

## Tokens

Authorization codes live 5 minutes and work once. Access tokens (1 h) and refresh tokens (30 d) are opaque, stored hashed, and refresh tokens rotate on use. A used, unknown, expired or revoked refresh token yields `400 {"error":"invalid_grant"}`. Revoking an API key invalidates every token issued from it. Expired rows are deleted daily.

## Tenant isolation

The company id is taken only from the authenticated principal (API key or token), never from a URL, body, or hub payload. The connection registry is keyed by company first, so a tool name from another company resolves to "not available". Tests: `McpEndpointTests`, `EndToEndTests.TwoCompanies_*`, `PortalApiTests.Keys_OtherCompany_*`.
