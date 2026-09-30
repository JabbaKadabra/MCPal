# Build context: repository root (see docker-compose.yml).
# The node stage for the MCPal.Web SPA is added in phase 11.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MCPal.Contracts/MCPal.Contracts.csproj src/MCPal.Contracts/
COPY src/MCPal.Cloud/MCPal.Cloud.csproj src/MCPal.Cloud/
RUN dotnet restore src/MCPal.Cloud/MCPal.Cloud.csproj
COPY src/ src/
RUN dotnet publish src/MCPal.Cloud/MCPal.Cloud.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "MCPal.Cloud.dll"]
