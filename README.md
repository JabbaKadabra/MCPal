# MCPal

A convenient option to securely enable remote MCP access.

MCPal lets claude.ai reach MCP servers inside a company network without opening any inbound port. A small bridge inside the network opens an outbound connection to the MCPal server. The server exposes one public MCP endpoint and relays tool calls through that tunnel to the bridge, which calls the local MCP servers.

```
claude.ai / Claude Code  ──HTTPS──►  MCPal.Server  ◄──outbound wss──  MCPal.Bridge ──► local MCP servers
                                     (/mcp, OAuth,                    (inside the company network,
                                      portal, tunnel hub)              no inbound ports)
```

Every company (tenant) sees only its own tools, and inside a company an owner decides which users may use which servers and tools. Local MCP servers learn who is calling. Design and implementation plan: [plan.md](plan.md); access control: [docs/access-control.md](docs/access-control.md).

## Quickstart (about 15 minutes)

### 1. Run the server

```bash
docker compose up --build        # server on http://localhost:8080 + PostgreSQL
```

For real use, put it behind TLS and set `Mcpal__PublicUrl` in `docker-compose.yml` to the public HTTPS URL (Claude only connects to public HTTPS servers, and the OAuth metadata is built from this URL).

### 2. Create your company and an API key

Open the server URL, choose **Create account** (you become the company's owner), then **API keys → Create key** with type **Bridge key**. The full key is shown once. A bridge key belongs to the company and only opens the tunnel: it is refused on `/mcp`, so a copy taken from a server is useless for calling tools. Colleagues get an account through **Users → Invite**; they sign in to Claude with it. Claude itself needs no key: it signs in with the MCPal account (OAuth). A **personal access token** (type "Personal access token") acts as its owner and is for tools that cannot do OAuth, e.g. Claude Code with a header.

Everyone may use every tool at first. To restrict, open **Groups**: remove or narrow the grant of the built-in *Everyone* group and give groups their own grants (server and tool patterns). Owners may always use everything.

### 3. Start the bridge inside your network

Download the bridge for your platform from the [releases](https://github.com/JabbaKadabra/MCPal/releases) page (`mcpal-bridge-<version>-linux-x64.tar.gz`, `linux-arm64`, or `win-x64.zip`; check the archive against `sha256sums.txt`) and install it as a service with the script inside:

```bash
# Linux (systemd)
tar -xzf mcpal-bridge-<version>-linux-x64.tar.gz && cd mcpal-bridge-<version>-linux-x64
sudo ./install.sh --api-key mcpal_…       # a bridge key; creates user mcpal, /opt/mcpal, /etc/mcpal, the unit
sudo nano /etc/mcpal/mcpal.json           # then: sudo systemctl restart mcpal-bridge
```

```powershell
# Windows (elevated PowerShell)
Expand-Archive mcpal-bridge-<version>-win-x64.zip . ; cd mcpal-bridge-<version>-win-x64
.\install.ps1 -ApiKey mcpal_…             # service MCPalBridge, C:\Program Files\MCPal, config in C:\ProgramData\MCPal
```

Both scripts keep an existing `mcpal.json`, protect the configuration (mode 640 and `bridge.env` 600 on Linux; an ACL for Administrators, SYSTEM and the service account on Windows), enable restart on failure and start the service only when `mcpal-bridge check` passes. `uninstall.sh` / `uninstall.ps1` remove it. The Linux unit runs with `ProtectSystem=strict` and `ProtectHome=yes`; a local server that needs other paths gets them through `systemctl edit mcpal-bridge` (see the comments in the unit). The Windows binary is unsigned unless the release says otherwise, so SmartScreen may warn.

To build the bridge yourself: `./bridge/packaging/publish-bridge.sh --version 1.1.0 linux-x64` (also `linux-arm64`, `win-x64`; without arguments all three) writes `artifacts/bridge/<rid>`, and `./bridge/packaging/package-bridge.sh 1.1.0 linux-x64` packs the release archive.

Or run the binary directly. `mcpal.json` sits next to it (or is given with `--config`); copy your existing `mcpServers` block as it is:

```json
{
  "mcpal": { "url": "https://mcpal.example.com", "bridgeName": "hq-01" },
  "mcpServers": {
    "kb":   { "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "…" } },
    "wiki": { "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer …" } }
  }
}
```

Local servers learn who is calling: the bridge puts the caller (a signed token plus claims) into `_meta["eu.nordstein.mcp/user"]` of every tool call. `"userTokenHeader": "Authorization"` on an HTTP server also sends the token in that header (with tool calls only), `"userContext": false` keeps a server from seeing callers, and `"jwksFile": "/etc/mcpal/jwks.json"` makes the bridge keep a copy of the server's public keys for servers without internet access. Servers must verify the token and refuse calls without a caller; see [docs/access-control.md](docs/access-control.md).

```bash
export MCPAL_API_KEY=mcpal_…          # or set mcpal.apiKey in the file
./MCPal.Bridge check --config mcpal.json   # validate the config, start the servers, list their tools
./MCPal.Bridge run   --config mcpal.json   # connect to the server (also runs as a Windows service / systemd unit)
```

Values in `command`, `args`, `env`, `url`, `headers` and in `mcpal.url` / `mcpal.apiKey` can refer to environment variables, so secrets stay out of the file: `"Authorization": "Bearer ${WIKI_TOKEN}"`, or with a default `"${WIKI_HOST:-intranet}"`. Write `$$` for a literal `$`. A variable that is not set and has no default stops the bridge (and `check`) with a message that names the server and the variable.

To expose only part of a local server, add `includeTools` and/or `excludeTools` to it. Patterns are case-sensitive globs (`*`, `?`). A tool must match an include pattern (when the list is set) and no exclude pattern: `"postgres": { "command": "…", "includeTools": ["query", "list_*"], "excludeTools": ["drop_*"] }`. Hidden tools do not reach the server and the bridge refuses calls to them; `check` lists them as "hidden by config".

For monitoring (Zabbix, Nagios, SCOM), set `"statusFile": "/var/lib/mcpal/status.json"` in `mcpal.json`. The bridge writes its tunnel state, registered servers with tool counts and rejections there on every change and every 30 s, replacing the file atomically. `./MCPal.Bridge status --config mcpal.json` prints it and exits with 1 when the tunnel is not connected or the file is older than two minutes.

The **Connections** page of the portal shows the bridge and its tools within seconds.

### 4. Connect Claude

- **claude.ai** (Team/Enterprise: an Owner adds the connector once, then **every member signs in individually**, so every Claude user needs an MCPal account): *Settings → Connectors → Add custom connector*, enter `https://<your-server>/mcp`, choose OAuth. Claude opens the MCPal sign-in page: sign in with your MCPal account and press **Connect as <email> (<company>)**. Claude then acts as you and sees only the tools you may use.
- **Claude Code**: `claude mcp add --transport http mcpal https://<your-server>/mcp`, or without OAuth, with a personal access token:
  `claude mcp add --transport http mcpal https://<your-server>/mcp --header "Authorization: Bearer mcpal_…"`.

Tools appear as `server__tool`, for example `kb__search`.

## Repository layout

Grouped by what ships: `server/` becomes the container image, `bridge/` the binary installed inside the company network.

| Path | Purpose |
|------|---------|
| `server/MCPal.Server` | ASP.NET Core host: MCP endpoint, OAuth 2.1 server, bridge hub, portal API, hosts the built SPA |
| `server/MCPal.Server.Tests` | Server unit and integration tests (PostgreSQL via Testcontainers) |
| `server/portal` | React + TypeScript + Vite SPA (portal pages and the OAuth sign-in page), built into the server's `wwwroot` |
| `bridge/MCPal.Bridge` | Worker inside the company network that connects local MCP servers to the MCPal server |
| `bridge/MCPal.Bridge.Tests` | Bridge unit tests |
| `bridge/packaging` | Publish and package scripts, Linux (systemd) and Windows service installers |
| `shared/MCPal.Contracts` | Tunnel protocol DTOs and hub interfaces (no SDK dependency), used by server and bridge |
| `tests/MCPal.E2E.Tests` | End-to-end tests: server host, real bridge, stdio test MCP server, SDK MCP client |
| `tests/MCPal.TestMcpServer` | stdio MCP server used by the tests (`echo`, `add`, `slow`, `fail`, `crash`, `whoami`) |
| `docs/` | [Tunnel protocol](docs/tunnel-protocol.md), [OAuth and authentication](docs/oauth.md), [Access control and caller identity](docs/access-control.md) |

`MCPal.slnx` contains every project. `server/MCPal.Server.slnf` and `bridge/MCPal.Bridge.slnf` open just one side.

## Development

Requirements: .NET 10 SDK, Docker (Testcontainers and compose), Node.js 24+ (SPA).

```bash
dotnet build MCPal.slnx                   # zero warnings expected (warnings are errors)
dotnet test MCPal.slnx                    # needs Docker; DB tests are marked inconclusive without it
dotnet run --project server/MCPal.Server  # http://localhost:8080 (needs PostgreSQL, see appsettings)
cd server/portal && npm ci && npm test    # SPA unit tests
cd server/portal && npm run dev           # SPA dev server on :5173, proxies to :8080
cd server/portal && npm run build         # writes the SPA into server/MCPal.Server/wwwroot
cd server/portal && npx playwright install chromium && npm run e2e   # browser smoke tests against a running stack (MCPAL_URL, default http://localhost:8080)
```

Configuration (environment variables use `__` for `:`):

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConnectionStrings__Mcpal` | – | PostgreSQL connection string |
| `Mcpal__PublicUrl` | `http://localhost:8080` | Public base URL, used in OAuth metadata and the MCP resource URL |
| `Mcpal__DataProtectionPath` | – | Directory for Data Protection keys (mount a volume). **Required in Production**: it protects the signing keys of the caller tokens |
| `Mcpal__UserContext__TokenLifetimeSeconds` | 300 | Lifetime of the signed caller token that local servers receive (30 to 900) |
| `Mcpal__UserContext__SigningKeyLifetimeDays` | 90 | How long a signing key signs before its successor takes over (7 to 365); successors are published 2 days ahead, retired keys stay in `/.well-known/jwks.json` for 7 days |
| `Mcpal__ToolCallTimeoutSeconds` | 120 | Timeout of a relayed tool call |
| `Mcpal__AccessTokenLifetimeMinutes` / `RefreshTokenLifetimeDays` | 60 / 30 | OAuth token lifetimes |
| `Mcpal__McpRequestsPerMinute` | 600 | Rate limit on `/mcp` per user (all their tokens share it); requests without a valid credential share a limit per client IP |
| `Mcpal__AuditRetentionDays` / `AuditQueueCapacity` | 90 / 10000 | How long audit rows are kept; entries the audit writer holds in memory before it drops new ones |
| `Mcpal__LatestBridgeVersion` | – | Version of the newest bridge release; older bridges get an "update available" hint in the portal |
| `Mcpal__MigrateOnStartup` | true | Apply EF Core migrations at startup |
| `Mcpal__TrustedProxyNetworks__0`, `__1`, … | – | CIDR networks of reverse proxies whose `X-Forwarded-For`/`X-Forwarded-Proto` are trusted (loopback always is), e.g. `172.18.0.0/16` |

Behind a TLS-terminating reverse proxy set `Mcpal__TrustedProxyNetworks__0` to the proxy's network, so rate limits see client addresses and cookies get the `Secure` flag. Headers from other senders are ignored, so clients cannot spoof their address. The proxy must allow WebSockets on `/hub/bridge` and must not buffer `/mcp` responses (streamable HTTP).

### Releases and bridge versions

A tag `vX.Y.Z` is the version: pushing it runs `.github/workflows/release.yml`, which runs the CI checks, builds the bridge for `linux-x64`, `linux-arm64` and `win-x64`, creates the GitHub release with the archives and `sha256sums.txt`, and pushes the server image to `ghcr.io/<owner>/mcpal-server:<version>` and `:latest`. Run the workflow by hand with `dry_run` to build without releasing. Windows binaries are signed with `osslsigncode` when the repository secrets `WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PFX_PASSWORD` exist (a certificate is a business decision; unsigned binaries trigger SmartScreen warnings).

The portal's Connections page shows each bridge's version. Set `Mcpal__LatestBridgeVersion` (e.g. `1.1.0`) to make it point out bridges that are older. A bridge whose protocol major version does not match the server logs "too old or too new for the server … Download the matching bridge from <url>" and checks again only every 15 minutes instead of registering every 30 seconds.

### Accounts, email and teams

New accounts must confirm their email address before a password login works (signup itself signs you in). A mail with a confirmation link is sent at signup, and **Forgot your password?** on the sign-in page sends a reset link. Accounts that existed before this feature were confirmed by the upgrade migration, so nobody is locked out.

Set an SMTP server to send mail. Without `Mcpal__Smtp__Host` the mails, including their links, are only written to the log (`docker compose logs server`), which is enough for development:

| Setting | Default | Meaning |
|---------|---------|---------|
| `Mcpal__Smtp__Host` | – | SMTP server; empty = log the mails instead |
| `Mcpal__Smtp__Port` | 587 | Port (465 uses implicit TLS) |
| `Mcpal__Smtp__User` / `Password` | – | Credentials, if the server needs them |
| `Mcpal__Smtp__From` | – | Sender address (required with a host) |
| `Mcpal__Smtp__UseTls` | true | STARTTLS; turn off only for a local test server |

A company can have several users. The user who signs up is an **owner**. Owners open **Users** in the portal to invite colleagues by email (the link works 7 days, one address can have one account), choose their role, cancel invitations, **disable** and enable users, view what a user may use, and remove users. Disabling or removing a user ends their portal session and revokes their personal access tokens and OAuth tokens at once (enabling does not restore tokens); bridge keys stay, because running bridges use them. The last active owner cannot be disabled or removed. **Members** see connections and connect info, see what they may use (**My access**), set their display name and create personal access tokens for themselves; they see and revoke only their own. Owners see all keys, who they belong to, the audit log and the groups.

### Access control

Owners manage **Groups** in the portal. A group has members and grants; a grant allows the tools matching tool patterns of the servers matching a server pattern (`*` and `?` globs; server names ignore case, tool names do not). A user may use the union of the grants of all their groups, and the built-in group *Everyone* contains all users (new companies start with `* / *` on it). Owners may use every tool. A forbidden tool is missing from `tools/list` and answers like an unknown tool. Changes apply to the next request. Details, the caller token and how local servers verify it: [docs/access-control.md](docs/access-control.md).

### Audit log

Every tool call through `/mcp` is written to the audit log of its company: time, duration, the **user** who made it, the personal access token or OAuth client used, bridge, server, tool and outcome (`ok`, `tool_error`, `timeout`, `offline`, `relay_error`, `cancelled`), plus a short error message. Arguments and results are not stored. The **Audit log** page of the portal (owners only, because it names people) filters by user, tool, outcome, key and time and exports CSV (up to 100 000 rows). The API is `GET /api/portal/audit` (keyset pagination with `cursor`) and `/api/portal/audit/export.csv`.

Rows are written in the background from a bounded queue (`Mcpal__AuditQueueCapacity`, default 10 000), so a slow database never slows a tool call; entries that do not fit are dropped and counted (`mcpal.audit.dropped`). `Mcpal__AuditRetentionDays` (default 90) deletes older rows hourly.

### Telemetry

The server and the bridge emit OpenTelemetry traces and metrics. Nothing is exported unless the standard variable `OTEL_EXPORTER_OTLP_ENDPOINT` is set (other `OTEL_*` variables such as `OTEL_EXPORTER_OTLP_HEADERS` work as usual). The server reports as `mcpal-server`, the bridge as `mcpal-bridge`. One tool call is one trace: the server HTTP request, the relay span `mcpal.tool_call` and the bridge's `mcpal.local_call` (the trace context travels through the tunnel).

| Metric | Type | Tags |
|--------|------|------|
| `mcpal.tool_calls` | counter | `outcome`: `ok`, `tool_error`, `timeout`, `offline`, `relay_error`, `cancelled` |
| `mcpal.tool_call.duration` | histogram (s) | `outcome` |
| `mcpal.tunnels.active` | gauge | – |
| `mcpal.bridge.registrations` | counter | `result`: `accepted`, `partial`, `rejected` |
| `mcpal.rate_limit.rejections` | counter | `policy` |

The company id is on spans (`mcpal.company_id`) but never a metric tag. For a local dashboard: `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire:18889 docker compose --profile otel up`, then open http://localhost:18888.

### Health endpoints

| Path | Meaning |
|------|---------|
| `/health/live` | The process is up (no dependency checks). Use it as liveness probe. |
| `/health/ready` | PostgreSQL is reachable. Returns 503 when it is not. Use it as readiness probe. |
| `/health` | Alias of `/health/ready`, for existing deployments. |

The endpoints are anonymous and return the status only (`Healthy` / `Unhealthy`). The compose file uses `/health/ready` as the container health check.

## Limits of the MVP

- Single server instance (the connection registry is in memory). Scale-out needs a backplane and routing of tool calls to the instance that owns the tunnel.
- Tools only: MCP resources and prompts are not relayed.
- No platform-admin UI; operate via the database and `dotnet ef`.

## License

[Elastic License 2.0](LICENSE).
