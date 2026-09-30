# Authentication and OAuth

MCPal identifies **people**. A tool call on `/mcp` is always made by a portal user, and the credentials are tied to that user. Two kinds of API keys exist (`mcpal_<8 char company id>_<40 random characters>`, stored as a SHA-256 hash, shown once, can expire, can be revoked), plus OAuth tokens:

| Credential | Belongs to | `/mcp` | Tunnel (`/hub/bridge`) |
|------------|------------|--------|------------------------|
| **OAuth access token** (claude.ai and Claude Code sign-in) | the user who signed in | yes | refused (`AuthKind=oauth`) |
| **Personal access token** (API key, `purpose: personal`) | the user who created it (and its company) | yes, as `Authorization: Bearer mcpal_…` | `403` |
| **Bridge key** (API key, `purpose: bridge`) | the company; only owners create it | `403` | yes |

A token or personal access token acts with the **current rights of its user** (see [access-control.md](access-control.md)). There are no service accounts.

## Discovery

A request to `/mcp` without valid credentials gets `401` with
`WWW-Authenticate: Bearer resource_metadata="<PublicUrl>/.well-known/oauth-protected-resource/mcp"`.

| Endpoint | Purpose |
|----------|---------|
| `GET /.well-known/oauth-protected-resource[/mcp]` (RFC 9728) | resource `<PublicUrl>/mcp`, authorization server, scope `mcp` |
| `GET /.well-known/oauth-authorization-server` (RFC 8414) | endpoints, `S256` only, public clients (`none`), `jwks_uri` |
| `GET /.well-known/jwks.json` | the public keys that verify caller tokens (see [access-control.md](access-control.md)) |
| `POST /oauth/register` (RFC 7591) | dynamic client registration; redirect URIs limited to `https://claude.ai/api/mcp/auth_callback` and loopback (`http://localhost` / `http://127.0.0.1`, any port) |
| `GET /oauth/authorize` | the React sign-in page |
| `GET /api/oauth/authorize/context`, `POST /api/oauth/authorize` | JSON API of that page; validates client, redirect URI, PKCE (`S256` required) and `resource` |
| `POST /oauth/token` | form-urlencoded only; `authorization_code` (PKCE) and `refresh_token` (rotation) |

## Sign-in

The OAuth sign-in is the **portal login**; pasting an API key is not possible. `/oauth/authorize` opens the React page:

1. Without a portal session the page redirects to `/login?returnUrl=/oauth/authorize?…`. Only relative paths are accepted as `returnUrl` (no open redirect). The login page says that an account needs an invitation from an owner and offers no sign-up there.
2. With a session the page shows **Connect as `<email>` (`<company>`)**, **Not you? Sign out** and **Cancel** (`access_denied` back to the client).
3. **Connect** posts to `/api/oauth/authorize` with the anti-forgery header. The endpoint needs the session (`401 login_required` without one, also for a disabled user or a disabled company) and binds the authorization code to the user and their company.

After any sign-in (login, signup, accepting an invitation) the anti-forgery token must be fetched again: it is bound to the signed-in user. The portal and its test clients do that.

Users who are disabled cannot sign in (the login answers like a wrong password) and existing portal sessions stop working on the next request.

## Tokens

Authorization codes live 5 minutes and work once. Access tokens (1 h) and refresh tokens (30 d) are opaque, stored hashed, bound to the user and their company, and refresh tokens rotate on use. A used, unknown, expired or revoked refresh token, or a refresh for a user who was disabled or removed since, yields `400 {"error":"invalid_grant"}`. Every request with an access token re-checks that the user exists, is not disabled, belongs to the token's company, and that the company is enabled.

**Disabling or removing a user** revokes their OAuth tokens and personal access tokens. Re-enabling the user does not restore them: the user connects Claude again. Expired rows are deleted daily.

## API keys

| Purpose | Who creates it | Use |
|---------|----------------|-----|
| `personal` (default) | any user, for themselves | bearer token on `/mcp`, e.g. Claude Code `--header "Authorization: Bearer mcpal_…"` |
| `bridge` | owners | `mcpal.json` / `MCPAL_API_KEY` of a bridge; opens tunnels only |

Purposes cannot be changed (revoke and create a new key). Owners see all keys and whose they are; members see and revoke only their own personal access tokens. Keys cannot be limited to servers any more: what a user may use is decided by their groups. Revoking a personal access token stops it at once; revoking a bridge key closes its tunnels at once.

## Tenant isolation

The company id is taken only from the authenticated principal (API key or token), never from a URL, body, or hub payload. The connection registry is keyed by company first, so a tool name from another company resolves to "not available". Every group, grant and audit query filters by the company explicitly. Tests: `McpEndpointTests`, `EndToEndTests.TwoCompanies_*`, `PortalApiTests.Keys_OtherCompany_*`, `AccessEndpointTests.Groups_IdsOfOtherCompany_*`, `AuditEndpointTests.Get_RowsOfOtherCompany_*`.
