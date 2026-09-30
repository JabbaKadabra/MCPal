# Build context: repository root (see docker-compose.yml).
FROM node:24-alpine AS web
WORKDIR /src/src/MCPal.Web
COPY src/MCPal.Web/package.json src/MCPal.Web/package-lock.json ./
RUN npm ci
COPY src/MCPal.Web/ ./
# vite.config.ts writes the build to ../MCPal.Cloud/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MCPal.Contracts/MCPal.Contracts.csproj src/MCPal.Contracts/
COPY src/MCPal.Cloud/MCPal.Cloud.csproj src/MCPal.Cloud/
RUN dotnet restore src/MCPal.Cloud/MCPal.Cloud.csproj
COPY src/MCPal.Contracts/ src/MCPal.Contracts/
COPY src/MCPal.Cloud/ src/MCPal.Cloud/
COPY --from=web /src/src/MCPal.Cloud/wwwroot src/MCPal.Cloud/wwwroot
RUN dotnet publish src/MCPal.Cloud/MCPal.Cloud.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Data Protection keys live on a volume; the non-root user must own it.
RUN mkdir -p /data/keys && chown $APP_UID /data/keys
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "MCPal.Cloud.dll"]
