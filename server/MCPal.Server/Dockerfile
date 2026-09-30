# Build context: repository root (see docker-compose.yml).
FROM node:24-alpine AS web
WORKDIR /src/server/portal
COPY server/portal/package.json server/portal/package-lock.json ./
RUN npm ci
COPY server/portal/ ./
# vite.config.ts writes the build to ../MCPal.Server/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/MCPal.Contracts/MCPal.Contracts.csproj shared/MCPal.Contracts/
COPY server/MCPal.Server/MCPal.Server.csproj server/MCPal.Server/
RUN dotnet restore server/MCPal.Server/MCPal.Server.csproj
COPY shared/MCPal.Contracts/ shared/MCPal.Contracts/
COPY server/MCPal.Server/ server/MCPal.Server/
COPY --from=web /src/server/MCPal.Server/wwwroot server/MCPal.Server/wwwroot
RUN dotnet publish server/MCPal.Server/MCPal.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Data Protection keys live on a volume; the non-root user must own it.
RUN mkdir -p /data/keys && chown $APP_UID /data/keys
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "MCPal.Server.dll"]
