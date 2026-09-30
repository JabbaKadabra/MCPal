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

Open the cloud URL, choose **Create account**, then **API keys → Create key**. The full key is shown once. One key type serves two purposes: the agent uses it to open the tunnel, and you paste it when Claude asks you to sign in.

### 3. Start the agent inside your network

Build the single-file agent (`./scripts/publish-agent.sh` produces `artifacts/agent/linux-x64` and `win-x64`) and create `mcpal.json` next to it. Copy your existing `mcpServers` block as it is:

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
```

Configuration (environment variables use `__` for `:`):

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConnectionStrings__Mcpal` | – | PostgreSQL connection string |
| `Mcpal__PublicUrl` | `http://localhost:8080` | Public base URL, used in OAuth metadata and the MCP resource URL |
| `Mcpal__DataProtectionPath` | – | Directory for Data Protection keys (mount a volume) |
| `Mcpal__ToolCallTimeoutSeconds` | 120 | Timeout of a relayed tool call |
| `Mcpal__AccessTokenLifetimeMinutes` / `RefreshTokenLifetimeDays` | 60 / 30 | OAuth token lifetimes |
| `Mcpal__McpRequestsPerMinute` | 600 | Rate limit per bearer token on `/mcp` |
| `Mcpal__MigrateOnStartup` | true | Apply EF Core migrations at startup |

Behind a TLS-terminating reverse proxy set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so rate limiting sees client addresses and cookies get the `Secure` flag; the proxy must allow WebSockets on `/hub/agent` and must not buffer `/mcp` responses (streamable HTTP).

## Limits of the MVP

- Single cloud instance (the connection registry is in memory). Scale-out needs a backplane and routing of tool calls to the instance that owns the tunnel.
- Tools only: MCP resources and prompts are not relayed.
- No platform-admin UI; operate via the database and `dotnet ef`.
- No password reset or email confirmation in the portal.

## License

[Elastic License 2.0](LICENSE).
