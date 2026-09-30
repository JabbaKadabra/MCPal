# MCPal

A convenient option to securely enable remote MCP access.

MCPal lets claude.ai reach MCP servers inside a company network without opening any inbound port. A small agent inside the network opens an outbound connection to the MCPal cloud. The cloud exposes one public MCP endpoint and relays tool calls through that tunnel to the agent, which calls the local MCP servers.

```
claude.ai / Claude Code  ──HTTPS──►  MCPal.Cloud  ◄──outbound wss──  MCPal.Agent ──► local MCP servers
                                     (/mcp, OAuth,                    (inside the company network,
                                      portal, tunnel hub)              no inbound ports)
```

Every company (tenant) sees only its own tools. Design and implementation plan: [plan.md](plan.md).

## Quickstart (about 15 minutes)

### 1. Run the cloud

```bash
docker compose up --build        # cloud on http://localhost:8080 + PostgreSQL
```

For real use, put it behind TLS and set `Mcpal__PublicUrl` in `docker-compose.yml` to the public HTTPS URL (Claude only connects to public HTTPS servers, and the OAuth metadata is built from this URL).

### 2. Create your company and an API key

Open the cloud URL, choose **Create account**, then **API keys → Create key**. The full key is shown once. Create two kinds of keys: an **agent key** for `mcpal.json` (opens the tunnel, refused on `/mcp` and at the Claude sign-in, so a copy taken from a server is useless from Claude) and a **Claude key** that you paste when Claude asks you to sign in (refused on tunnels). A Claude key can be limited to some servers, so a team sees only `jira__*` tools, and can expire. Keys from earlier versions keep working for both.

### 3. Start the agent inside your network

Download the agent for your platform from the [releases](https://github.com/JabbaKadabra/MCPal/releases) page (`mcpal-agent-<version>-linux-x64.tar.gz`, `linux-arm64`, or `win-x64.zip`; check the archive against `sha256sums.txt`) and install it as a service with the script inside:

```bash
# Linux (systemd)
tar -xzf mcpal-agent-<version>-linux-x64.tar.gz && cd mcpal-agent-<version>-linux-x64
sudo ./install.sh --api-key mcpal_…       # an agent key; creates user mcpal, /opt/mcpal, /etc/mcpal, the unit
sudo nano /etc/mcpal/mcpal.json           # then: sudo systemctl restart mcpal-agent
```

```powershell
# Windows (elevated PowerShell)
Expand-Archive mcpal-agent-<version>-win-x64.zip . ; cd mcpal-agent-<version>-win-x64
.\install.ps1 -ApiKey mcpal_…             # service MCPalAgent, C:\Program Files\MCPal, config in C:\ProgramData\MCPal
```

Both scripts keep an existing `mcpal.json`, protect the configuration (mode 640 and `agent.env` 600 on Linux; an ACL for Administrators, SYSTEM and the service account on Windows), enable restart on failure and start the service only when `mcpal-agent check` passes. `uninstall.sh` / `uninstall.ps1` remove it. The Linux unit runs with `ProtectSystem=strict` and `ProtectHome=yes`; a local server that needs other paths gets them through `systemctl edit mcpal-agent` (see the comments in the unit). The Windows binary is unsigned unless the release says otherwise, so SmartScreen may warn.

To build the agent yourself: `./scripts/publish-agent.sh --version 1.1.0 linux-x64` (also `linux-arm64`, `win-x64`; without arguments all three) writes `artifacts/agent/<rid>`, and `./scripts/package-agent.sh 1.1.0 linux-x64` packs the release archive.

Or run the binary directly. `mcpal.json` sits next to it (or is given with `--config`); copy your existing `mcpServers` block as it is:

```json
{
  "cloud": { "url": "https://mcpal.example.com", "agentName": "hq-01" },
  "mcpServers": {
    "kb":   { "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "…" } },
    "wiki": { "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer …" } }
  }
}
```

```bash
export MCPAL_API_KEY=mcpal_…          # or set cloud.apiKey in the file
./MCPal.Agent check --config mcpal.json   # validate the config, start the servers, list their tools
./MCPal.Agent run   --config mcpal.json   # connect to the cloud (also runs as a Windows service / systemd unit)
```

Values in `command`, `args`, `env`, `url`, `headers` and in `cloud.url` / `cloud.apiKey` can refer to environment variables, so secrets stay out of the file: `"Authorization": "Bearer ${WIKI_TOKEN}"`, or with a default `"${WIKI_HOST:-intranet}"`. Write `$$` for a literal `$`. A variable that is not set and has no default stops the agent (and `check`) with a message that names the server and the variable.

To expose only part of a local server, add `includeTools` and/or `excludeTools` to it. Patterns are case-sensitive globs (`*`, `?`). A tool must match an include pattern (when the list is set) and no exclude pattern: `"postgres": { "command": "…", "includeTools": ["query", "list_*"], "excludeTools": ["drop_*"] }`. Hidden tools do not reach the cloud and the agent refuses calls to them; `check` lists them as "hidden by config".

For monitoring (Zabbix, Nagios, SCOM), set `"statusFile": "/var/lib/mcpal/status.json"` in `mcpal.json`. The agent writes its tunnel state, registered servers with tool counts and rejections there on every change and every 30 s, replacing the file atomically. `./MCPal.Agent status --config mcpal.json` prints it and exits with 1 when the tunnel is not connected or the file is older than two minutes.

The **Connections** page of the portal shows the agent and its tools within seconds.

### 4. Connect Claude

- **claude.ai** (Team/Enterprise: an Owner does this once): *Settings → Connectors → Add custom connector*, enter `https://<your-cloud>/mcp`, choose OAuth. On the sign-in page paste one of your API keys (or press **Connect as <company>** when you are signed in to the portal).
- **Claude Code**: `claude mcp add --transport http mcpal https://<your-cloud>/mcp`, or without OAuth:
  `claude mcp add --transport http mcpal https://<your-cloud>/mcp --header "Authorization: Bearer mcpal_…"`.

Tools appear as `server__tool`, for example `kb__search`.

## Repository layout

| Path | Purpose |
|------|---------|
| `src/MCPal.Contracts` | Tunnel protocol DTOs and hub interfaces (no SDK dependency) |
| `src/MCPal.Cloud` | ASP.NET Core host: MCP endpoint, OAuth 2.1 server, agent hub, portal API, hosts the built SPA |
| `src/MCPal.Web` | React + TypeScript + Vite portal SPA (portal pages and the OAuth sign-in page) |
| `src/MCPal.Agent` | Worker that connects local MCP servers to the cloud |
| `tests/MCPal.Cloud.Tests` | Cloud unit and integration tests (PostgreSQL via Testcontainers) |
| `tests/MCPal.Agent.Tests` | Agent unit tests |
| `tests/MCPal.E2E.Tests` | End-to-end tests: cloud host, real agent, stdio test MCP server, SDK MCP client |
| `tests/MCPal.TestMcpServer` | stdio MCP server used by the tests (`echo`, `add`, `slow`, `fail`, `crash`) |
| `docs/` | [Tunnel protocol](docs/tunnel-protocol.md), [OAuth and authentication](docs/oauth.md) |

## Development

Requirements: .NET 10 SDK, Docker (Testcontainers and compose), Node.js 24+ (SPA).

```bash
dotnet build MCPal.sln                    # zero warnings expected (warnings are errors)
dotnet test MCPal.sln                     # needs Docker; DB tests are marked inconclusive without it
dotnet run --project src/MCPal.Cloud      # http://localhost:8080 (needs PostgreSQL, see appsettings)
cd src/MCPal.Web && npm ci && npm test    # SPA unit tests
cd src/MCPal.Web && npm run dev           # SPA dev server on :5173, proxies to :8080
cd src/MCPal.Web && npm run build         # writes the SPA into src/MCPal.Cloud/wwwroot
cd src/MCPal.Web && npx playwright install chromium && npm run e2e   # browser smoke tests against a running stack (MCPAL_URL, default http://localhost:8080)
```

Configuration (environment variables use `__` for `:`):

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConnectionStrings__Mcpal` | – | PostgreSQL connection string |
| `Mcpal__PublicUrl` | `http://localhost:8080` | Public base URL, used in OAuth metadata and the MCP resource URL |
| `Mcpal__DataProtectionPath` | – | Directory for Data Protection keys (mount a volume) |
| `Mcpal__ToolCallTimeoutSeconds` | 120 | Timeout of a relayed tool call |
| `Mcpal__AccessTokenLifetimeMinutes` / `RefreshTokenLifetimeDays` | 60 / 30 | OAuth token lifetimes |
| `Mcpal__McpRequestsPerMinute` | 600 | Rate limit on `/mcp` per API key (OAuth tokens count against the key they were issued from, else their company); requests without a valid credential share a limit per client IP |
| `Mcpal__AuditRetentionDays` / `AuditQueueCapacity` | 90 / 10000 | How long audit rows are kept; entries the audit writer holds in memory before it drops new ones |
| `Mcpal__LatestAgentVersion` | – | Version of the newest agent release; older agents get an "update available" hint in the portal |
| `Mcpal__MigrateOnStartup` | true | Apply EF Core migrations at startup |
| `Mcpal__TrustedProxyNetworks__0`, `__1`, … | – | CIDR networks of reverse proxies whose `X-Forwarded-For`/`X-Forwarded-Proto` are trusted (loopback always is), e.g. `172.18.0.0/16` |

Behind a TLS-terminating reverse proxy set `Mcpal__TrustedProxyNetworks__0` to the proxy's network, so rate limits see client addresses and cookies get the `Secure` flag. Headers from other senders are ignored, so clients cannot spoof their address. The proxy must allow WebSockets on `/hub/agent` and must not buffer `/mcp` responses (streamable HTTP).

### Releases and agent versions

A tag `vX.Y.Z` is the version: pushing it runs `.github/workflows/release.yml`, which runs the CI checks, builds the agent for `linux-x64`, `linux-arm64` and `win-x64`, creates the GitHub release with the archives and `sha256sums.txt`, and pushes the cloud image to `ghcr.io/<owner>/mcpal-cloud:<version>` and `:latest`. Run the workflow by hand with `dry_run` to build without releasing. Windows binaries are signed with `osslsigncode` when the repository secrets `WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PFX_PASSWORD` exist (a certificate is a business decision; unsigned binaries trigger SmartScreen warnings).

The portal's Connections page shows each agent's version. Set `Mcpal__LatestAgentVersion` (e.g. `1.1.0`) to make it point out agents that are older. An agent whose protocol major version does not match the cloud logs "too old or too new for the cloud … Download the matching agent from <url>" and checks again only every 15 minutes instead of registering every 30 seconds.

### Accounts, email and teams

New accounts must confirm their email address before a password login works (signup itself signs you in). A mail with a confirmation link is sent at signup, and **Forgot your password?** on the sign-in page sends a reset link. Accounts that existed before this feature were confirmed by the upgrade migration, so nobody is locked out.

Set an SMTP server to send mail. Without `Mcpal__Smtp__Host` the mails, including their links, are only written to the log (`docker compose logs cloud`), which is enough for development:

| Setting | Default | Meaning |
|---------|---------|---------|
| `Mcpal__Smtp__Host` | – | SMTP server; empty = log the mails instead |
| `Mcpal__Smtp__Port` | 587 | Port (465 uses implicit TLS) |
| `Mcpal__Smtp__User` / `Password` | – | Credentials, if the server needs them |
| `Mcpal__Smtp__From` | – | Sender address (required with a host) |
| `Mcpal__Smtp__UseTls` | true | STARTTLS; turn off only for a local test server |

A company can have several users. The user who signs up is an **owner**. Owners open **Users** in the portal to invite colleagues by email (the link works 7 days, one address can have one account), choose their role, cancel invitations and remove users. Removing a user revokes the Claude keys they created; keys for agents stay, because running agents use them. The last owner cannot be removed. **Members** see connections, the audit log and connect info and create Claude keys (purpose "client") for themselves; they see and revoke only their own keys. Owners see all keys and who created them.

### Audit log

Every tool call through `/mcp` is written to the audit log of its company: time, duration, the API key or OAuth client that made it, agent, server, tool and outcome (`ok`, `tool_error`, `timeout`, `offline`, `relay_error`, `cancelled`), plus a short error message. Arguments and results are not stored. The **Audit log** page of the portal filters by tool, outcome, key and time and exports CSV (up to 100 000 rows). The API is `GET /api/portal/audit` (keyset pagination with `cursor`) and `/api/portal/audit/export.csv`.

Rows are written in the background from a bounded queue (`Mcpal__AuditQueueCapacity`, default 10 000), so a slow database never slows a tool call; entries that do not fit are dropped and counted (`mcpal.audit.dropped`). `Mcpal__AuditRetentionDays` (default 90) deletes older rows hourly.

### Telemetry

The cloud and the agent emit OpenTelemetry traces and metrics. Nothing is exported unless the standard variable `OTEL_EXPORTER_OTLP_ENDPOINT` is set (other `OTEL_*` variables such as `OTEL_EXPORTER_OTLP_HEADERS` work as usual). The cloud reports as `mcpal-cloud`, the agent as `mcpal-agent`. One tool call is one trace: the cloud HTTP request, the relay span `mcpal.tool_call` and the agent's `mcpal.local_call` (the trace context travels through the tunnel).

| Metric | Type | Tags |
|--------|------|------|
| `mcpal.tool_calls` | counter | `outcome`: `ok`, `tool_error`, `timeout`, `offline`, `relay_error`, `cancelled` |
| `mcpal.tool_call.duration` | histogram (s) | `outcome` |
| `mcpal.tunnels.active` | gauge | – |
| `mcpal.agent.registrations` | counter | `result`: `accepted`, `partial`, `rejected` |
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

- Single cloud instance (the connection registry is in memory). Scale-out needs a backplane and routing of tool calls to the instance that owns the tunnel.
- Tools only: MCP resources and prompts are not relayed.
- No platform-admin UI; operate via the database and `dotnet ef`.

## License

[Elastic License 2.0](LICENSE).
