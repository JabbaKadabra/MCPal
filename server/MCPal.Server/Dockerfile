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
COPY server/Directory.Build.props server/
COPY shared/MCPal.Contracts/MCPal.Contracts.csproj shared/MCPal.Contracts/
COPY server/MCPal.Server.Domain/MCPal.Server.Domain.csproj server/MCPal.Server.Domain/
COPY server/MCPal.Server.Application/MCPal.Server.Application.csproj server/MCPal.Server.Application/
COPY server/MCPal.Server.Storage/MCPal.Server.Storage.csproj server/MCPal.Server.Storage/
COPY server/MCPal.Server.Infrastructure/MCPal.Server.Infrastructure.csproj server/MCPal.Server.Infrastructure/
COPY server/MCPal.Server.Web/MCPal.Server.Web.csproj server/MCPal.Server.Web/
COPY server/MCPal.Server/MCPal.Server.csproj server/MCPal.Server/
RUN dotnet restore server/MCPal.Server/MCPal.Server.csproj
COPY shared/MCPal.Contracts/ shared/MCPal.Contracts/
COPY server/MCPal.Server.Domain/ server/MCPal.Server.Domain/
COPY server/MCPal.Server.Application/ server/MCPal.Server.Application/
COPY server/MCPal.Server.Storage/ server/MCPal.Server.Storage/
COPY server/MCPal.Server.Infrastructure/ server/MCPal.Server.Infrastructure/
COPY server/MCPal.Server.Web/ server/MCPal.Server.Web/
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
