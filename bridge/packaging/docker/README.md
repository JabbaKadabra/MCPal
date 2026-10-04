# MCPal bridge as a container

The default way to run the bridge. The image `ghcr.io/jabbakadabra/mcpal-bridge` (linux/amd64 and linux/arm64, tags `<version>` and `latest`) holds the bridge plus Node (`npx`) and uv (`uvx`), so common stdio MCP servers start without more setup. The portal's **Setup** page shows the commands with your key filled in.

```bash
docker run -d --name mcpal-bridge --hostname mcpal-bridge --restart unless-stopped \
  -e MCPAL_URL=https://mcpal.example.com \
  -e MCPAL_API_KEY=mcpal_... \
  -v "$PWD/mcp.json:/config/mcp.json:ro" \
  -v mcpal-bridge-data:/data \
  ghcr.io/jabbakadabra/mcpal-bridge:latest
docker logs -f mcpal-bridge
```

Or use [`compose.yml`](compose.yml) in this folder.

## Contract

| What | Where |
|---|---|
| Server URL | `MCPAL_URL` (required) |
| Bridge key | `MCPAL_API_KEY` (required) |
| Local MCP servers | `/config/mcp.json`, mounted read-only (required; `.mcp.json` format of Claude Code) |
| JWKS copy, status file, tool caches (`HOME`) | `/data`, a volume; the container runs as a non-root user |
| Secrets for `mcp.json` | more `-e NAME=...`; refer to them as `${NAME}` in `mcp.json` |
| Other bridge options (`serverOptions`, `callTimeoutSeconds`, a fixed bridge name, ...) | mount your own `mcpal.json` over `/app/mcpal.json` (start from [`mcpal.json`](mcpal.json) here) |
| Bridge name in the portal | `--hostname` (default: the container id) |
| Health | `HEALTHCHECK` runs `status`: healthy only while the tunnel is connected |

`docker run ... check` validates the config, starts the local servers, lists their tools and exits.

## Local servers on the host or in other containers

`localhost` inside the container is the container. Reach a server on the host with `host.docker.internal` (add `--add-host=host.docker.internal:host-gateway` on Linux), or put the bridge on the Docker network of the other containers (`--network`). A local server that checks the caller token (`userTokenHeader`) reads the JWKS the bridge writes to `/data/jwks.json`: share the `/data` volume with that container.

## Other runtimes

The image has no Python, Java or .NET. Derive your own:

```dockerfile
FROM ghcr.io/jabbakadabra/mcpal-bridge:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
```

## Build it yourself

```bash
docker build -f bridge/Dockerfile --build-arg VERSION=1.1.0 -t mcpal-bridge .   # from the repository root
bridge/packaging/docker/test-image.sh                                           # builds and checks it
```
