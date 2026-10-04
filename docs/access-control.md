# Access control and caller identity

MCPal knows **people**, not only companies. Every tool call on `/mcp` is made by a portal user, and two things follow from that:

1. **Access control**: an owner decides which servers and tools each user may use (groups with grants).
2. **Caller identity**: local MCP servers learn who is calling, so they can do their own rights checks.

## Who is calling

| Credential | Belongs to | Works on `/mcp` | Opens tunnels |
|------------|------------|-----------------|---------------|
| OAuth token (claude.ai, Claude Code sign-in) | the portal user who signed in | yes | no |
| Personal access token (`purpose: personal`) | the user who created it | yes | no |
| Bridge key (`purpose: bridge`) | the company (created by an owner) | no (403) | yes |

There are no service accounts: every call has a human behind it. A token or personal access token acts with **the current rights of its user** and stops working when the user is disabled or removed (see [User lifecycle](#user-lifecycle)). Users are invited by an owner and sign in to the portal; the OAuth sign-in page is the portal login, so a person who has no MCPal account cannot connect Claude. The user model is prepared for SSO (`ExternalIssuer`, `ExternalSubject` on the user, `ExternalId` on groups), SSO itself is not implemented.

## Groups and grants

- A **group** has a name and members. Every company has exactly one built-in group, **Everyone**: all active users are members implicitly. It cannot be renamed, deleted or given a member list, but its grants can be changed.
- A **grant** belongs to a group and has a **server pattern** and one or more **tool patterns**. It allows the matching tools of the matching servers.
- A user may use everything that any of their groups (including Everyone) allows: the **union** of all grants.
- **Owners may use every tool**, whatever the grants say. Owners cannot lock themselves out.
- A new company starts with the grant `* / *` on Everyone, so the first 15 minutes work without configuration. To restrict, remove that grant (or narrow it) and give groups their own grants. Companies that existed before access control got the same default grant.

Rights are evaluated **live on every request** from a policy that the server caches in memory for up to 5 minutes and drops on every change (group, grant, membership, user disabled or removed, display name), so a change applies to the next request.

### Patterns

Glob patterns with `*` (any characters) and `?` (exactly one character). Everything else is literal.

| | Matches against | Case |
|--|-----------------|------|
| Server pattern | the server name as configured in the bridge (`mcpServers` key) | ignored |
| Tool pattern | the tool's name on the local server (not `server__tool`) | significant |

Tool patterns use letters, digits and `_ - . * ?` only; `*` means all tools. Examples: server `hr`, tools `list_*`, `get_?`; server `wiki*`, tools `*`.

### What a forbidden tool looks like

A tool the user may not use is **missing from `tools/list`**, and calling it by name answers exactly like a tool that does not exist (`Tool '<name>' is not available (bridge offline or unknown tool).`, audit outcome `offline`). Nothing reveals that the tool exists. A client that cached an old `tools/list` and calls a tool after its grant was removed gets that same answer.

### User lifecycle

- **Disable** (owner, Users page): the user is signed out, their personal access tokens and OAuth tokens are revoked, and they cannot sign in until an owner enables them again. Re-enabling restores the account, not the tokens: the user signs in and connects Claude again.
- **Remove**: the same, and the account is deleted.
- The last active owner cannot be disabled or removed.
- Every request re-checks that the user exists, is not disabled, belongs to the company in the credential and that the company is not disabled. A disabled user's bearer tokens fail with `401` at once, not at expiry.

## Caller identity for local MCP servers

For every tool call the server sends the caller to the bridge (tunnel protocol 1.2 or newer, `CallToolRequest.User`). The bridge puts it into the `tools/call` request to the local server:

```json
{
  "method": "tools/call",
  "params": {
    "name": "salaries",
    "arguments": { "...": "..." },
    "_meta": {
      "eu.nordstein.mcp/user": {
        "token": "eyJhbGciOiJFUzI1NiIs...",
        "sub": "0b6f1c0e-...",
        "email": "anna@acme.example",
        "name": "Anna Example",
        "groups": ["Everyone", "hr"],
        "companyId": "7f3c6a1e-...",
        "company": "acme"
      }
    }
  }
}
```

- **stdio servers** read `params._meta["eu.nordstein.mcp/user"]`.
- **HTTP servers** get the same `_meta` and can additionally receive the token in a header: set `userTokenHeader` for the server in `mcpal.json`. `Authorization` sends `Authorization: Bearer <token>`, any other name sends the raw token. The header is sent only with tool calls, never with the handshake, pings or background streams, so a user's token cannot leak into other requests.
- The bridge builds the request itself and never forwards Claude's `_meta`. It also removes the `eu.nordstein.mcp/user` key before it sets its own, so nothing can spoof it.
- Bridges older than 1.2 pass no caller (the Connections page marks them "no caller"). **Local servers must fail closed**: a call without a caller is refused, never treated as "anonymous but allowed".

### Per-server bridge settings

```json
{
  "jwksFile": "/etc/mcpal/jwks.json",
  "mcpServers": {
    "hr":    { "command": "hr-mcp" },
    "wiki":  { "url": "http://intranet:8080/mcp", "userTokenHeader": "Authorization" },
    "third": { "url": "http://intranet:8081/mcp", "headers": { "Authorization": "Bearer ${THIRD_TOKEN}" }, "userContext": false }
  }
}
```

| Setting | Meaning |
|---------|---------|
| `userContext` (default `true`) | `false`: this server gets no caller at all (no `_meta` entry, no header), e.g. a third-party server that should not see tokens |
| `userTokenHeader` | HTTP servers only. The header that carries the token. Configuration errors: on a stdio server, together with `userContext: false`, an invalid header name, or the same header also in `headers` |
| `jwksFile` (top level) | path of a copy of the MCPal server's JWKS. The bridge writes it at startup and hourly (atomically: temp file, then rename), so local servers without internet access can verify tokens. A failed fetch keeps the old file |

### The token

A JWT, signed with ES256, `typ: mcpal-user+jwt` (RFC 8725 explicit typing), `kid` = the key's thumbprint.

| Claim | Value |
|-------|-------|
| `iss` | `Mcpal:PublicUrl` without trailing slash |
| `aud` | `mcpal:<company slug>/<server name>`, e.g. `mcpal:acme/hr`. The portal's Connections page shows it per server, with a copy button. The slug never changes |
| `sub`, `email`, `name` | user id, email, display name (falls back to the email) |
| `groups` | group names including `Everyone` |
| `mcpal_role` | `owner` or `member` |
| `mcpal_company_id`, `mcpal_company` | company id and slug |
| `mcpal_tool` | the tool's name on the local server |
| `mcpal_auth` | `oauth` or `pat` |
| `jti` | the request id of the call |
| `iat`, `nbf`, `exp` | `exp = iat + 300 s` (`Mcpal:UserContext:TokenLifetimeSeconds`, 30 to 900) |

The public keys are at `GET /.well-known/jwks.json` (anonymous, `Cache-Control: public, max-age=300`, also announced as `jwks_uri` in the authorization server metadata). Keys are global and rotate: a key signs for 90 days (`Mcpal:UserContext:SigningKeyLifetimeDays`, 7 to 365), its successor is published 2 days before it takes over, and a retired key stays in the JWKS for 7 more days. **Cache the JWKS (5 minutes is fine) and look keys up by `kid`; refetch when the `kid` is unknown.**

The private keys are stored in the database, protected by ASP.NET Data Protection. Keep `Mcpal:DataProtectionPath` on a persistent volume (it is required in Production): with a lost key ring the server cannot read its signing keys, creates a new one and logs an error; verifiers that refresh the JWKS recover, verifiers with a stale copy fail until they do.

### Trust model

- **stdio servers**: the bridge starts the process and is the only writer of its stdin, so a `_meta` entry is as trustworthy as the bridge. Such servers may use the plain claims without verifying the token. A stolen bridge key cannot forge identities (the tunnel carries identities from the server; the bridge has no signing key).
- **HTTP servers** are reachable by anyone on the network. They **must verify the token**; `sub`, `email` and `groups` alone prove nothing there.
- What to check: signature against the JWKS (by `kid`, only `ES256`), `typ` = `mcpal-user+jwt`, `iss`, **`aud` = `mcpal:<company>/<your server name>`**, `exp`/`nbf`, and `mcpal_tool` = the tool being called (a token for `search` must not authorize `delete`). Recommended: remember `jti` values until `exp` and refuse repeats, and use HTTPS between bridge and server.
- **Fail closed**: no `_meta` entry, no token, an invalid token or a claim mismatch means "refuse".
- **Never log the token** (it is a bearer credential for up to 5 minutes).
- A **stdio server is shared by all users** of the company: one process serves every call. It must take the user from each request's `_meta`, never from process state such as a variable set by an earlier call.
- Tokens are replayable inside their 5 minutes against the one company/server/tool they name. The `jti` cache closes that gap.

### Verifying the token

Node.js (`jose`):

```js
import { createLocalJWKSet, jwtVerify } from 'jose';
import { readFile } from 'node:fs/promises';

const jwks = createLocalJWKSet(JSON.parse(await readFile('/etc/mcpal/jwks.json', 'utf8')));

export async function callerOf(meta, tool) {
  const token = meta?.['eu.nordstein.mcp/user']?.token;
  if (!token) throw new Error('no caller: refused'); // fail closed
  const { payload } = await jwtVerify(token, jwks, {
    issuer: 'https://mcpal.example.com',
    audience: 'mcpal:acme/hr',
    typ: 'mcpal-user+jwt',
    algorithms: ['ES256'],
  });
  if (payload.mcpal_tool !== tool) throw new Error('token is for another tool');
  return payload; // sub, email, name, groups, mcpal_role, ...
}
```

Python (`PyJWT[crypto]`):

```python
import json
import jwt

with open('/etc/mcpal/jwks.json') as f:
    jwks = jwt.PyJWKSet.from_dict(json.load(f))

def caller_of(meta, tool):
    token = ((meta or {}).get('eu.nordstein.mcp/user') or {}).get('token')
    if not token:
        raise PermissionError('no caller: refused')  # fail closed
    header = jwt.get_unverified_header(token)
    if header.get('typ') != 'mcpal-user+jwt':
        raise PermissionError('wrong token type')
    claims = jwt.decode(
        token, jwks[header['kid']].key, algorithms=['ES256'],
        issuer='https://mcpal.example.com', audience='mcpal:acme/hr',
    )
    if claims.get('mcpal_tool') != tool:
        raise PermissionError('token is for another tool')
    return claims
```

C# (`Microsoft.IdentityModel.JsonWebTokens`):

```csharp
var jwks = new JsonWebKeySet(File.ReadAllText("/etc/mcpal/jwks.json"));
var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
{
    ValidIssuer = "https://mcpal.example.com",
    ValidAudience = "mcpal:acme/hr",
    ValidTypes = ["mcpal-user+jwt"],
    ValidAlgorithms = ["ES256"],
    IssuerSigningKeys = jwks.GetSigningKeys(),
});
if (!result.IsValid || new JsonWebToken(token).GetPayloadValue<string>("mcpal_tool") != tool)
{
    throw new UnauthorizedAccessException(); // fail closed
}
```

## Audit and privacy

Every tool call is written to the audit log with the **user** (id, shown as the current email), the credential kind (`pat` or `oauth`), tool, outcome and duration. Arguments and results are not stored. The audit page and its CSV export are **for owners only**. The log names people: talk to your works council or data protection officer before you switch it on for employees, and set `Mcpal:AuditRetentionDays` accordingly (default 90). Audit rows keep the user id after a user is removed; the email is shown only while the account exists.

## Limits and follow-ups

- **claude.ai Team and Enterprise**: an owner adds the connector once, but **each member signs in individually**, so every Claude user needs an invited MCPal account. Bulk invitation and domain auto-join are follow-ups before SSO.
- **Stale tool lists**: Claude may keep a `tools/list` from before a grant change; a call then answers "not available". That is acceptable and fixes itself on the next list.
- **Group renames** break mappings that local servers keep by group name (`groups` carries names). SSO will bring stable group ids.
- **Single instance**: the policy cache is invalidated in-process, like the tunnel registry. Scale-out needs an invalidation signal between instances (pub/sub or a policy version column).
