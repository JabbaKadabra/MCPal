# MCPal

A convenient option to securely enable remote MCP access.

MCPal lets claude.ai reach MCP servers inside a company network without opening any inbound port. A small agent inside the network opens an outbound connection to the MCPal cloud. The cloud exposes one public MCP endpoint and relays tool calls through that tunnel to the agent, which calls the local MCP servers.

Design and implementation plan: [plan.md](plan.md).

## Repository layout

| Path | Purpose |
|------|---------|
| `src/MCPal.Contracts` | Tunnel protocol DTOs and hub interfaces (no SDK dependency) |
| `src/MCPal.Cloud` | ASP.NET Core host: MCP endpoint, OAuth 2.1 server, agent hub, portal API |
| `src/MCPal.Web` | React + TypeScript + Vite portal SPA (added in phase 10) |
| `src/MCPal.Agent` | Worker that connects local MCP servers to the cloud |
| `tests/MCPal.Cloud.Tests` | Cloud unit and integration tests |
| `tests/MCPal.Agent.Tests` | Agent unit tests |
| `tests/MCPal.E2E.Tests` | End-to-end tests: cloud host, in-process agent, test MCP server |
| `tests/MCPal.TestMcpServer` | stdio MCP server used by the tests |

## Development

Requirements: .NET 10 SDK, Docker (for Testcontainers and compose), Node.js (for the SPA).

```bash
dotnet build MCPal.sln
dotnet test MCPal.sln
dotnet run --project src/MCPal.Cloud      # http://localhost:8080
docker compose up --build                 # cloud + PostgreSQL
```

## License

[Elastic License 2.0](LICENSE).
