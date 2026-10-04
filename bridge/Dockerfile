# The MCPal bridge as a container image. Build context: repository root.
#
#   docker build -f bridge/Dockerfile --build-arg VERSION=1.1.0 -t mcpal-bridge .
#
# The image carries Node (npx) and uv (uvx) so that the usual stdio MCP servers start without a derived image. For anything
# else use `FROM ghcr.io/<owner>/mcpal-bridge` and install it. Run: see bridge/packaging/docker/README.md.
# The build stage always runs on the build machine: the output is architecture independent (no apphost), so the arm64
# image needs no emulated compile.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/MCPal.Contracts/MCPal.Contracts.csproj shared/MCPal.Contracts/
COPY bridge/MCPal.Bridge/MCPal.Bridge.csproj bridge/MCPal.Bridge/
RUN dotnet restore bridge/MCPal.Bridge/MCPal.Bridge.csproj
COPY shared/MCPal.Contracts/ shared/MCPal.Contracts/
COPY bridge/MCPal.Bridge/ bridge/MCPal.Bridge/
RUN dotnet publish bridge/MCPal.Bridge/MCPal.Bridge.csproj -c Release -o /app --no-restore -p:UseAppHost=false -p:Version=$VERSION

FROM ghcr.io/astral-sh/uv:0.9 AS uv
FROM node:24-slim AS node

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx
COPY --from=uv /uv /uvx /usr/local/bin/

WORKDIR /app
COPY --from=build /app .
COPY bridge/packaging/docker/mcpal.json /app/mcpal.json

# /config: mount mcp.json (the local MCP servers) read-only. /data: JWKS copy, status file, tool caches (HOME).
# The non-root user must own /data.
RUN mkdir -p /config /data/home && chown -R $APP_UID /data
ENV HOME=/data/home
VOLUME /data
USER $APP_UID

# `status` exits 0 only while the tunnel is connected and the status file is fresh.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["dotnet", "/app/MCPal.Bridge.dll", "status"]
ENTRYPOINT ["dotnet", "/app/MCPal.Bridge.dll"]
CMD ["run"]
