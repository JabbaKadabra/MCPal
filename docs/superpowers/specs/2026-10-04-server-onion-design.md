# MCPal.Server onion split

Date: 2026-10-04. Status: implemented 2026-10-04.

## Goal

Split the single `MCPal.Server` project into onion rings so that the compiler enforces the dependency direction. No behavior change: same endpoints, same wire protocol, same database schema, same namespaces.

## Rings

```
MCPal.Server (host) -> Web -> Application -> Domain -> Contracts
        |                                      ^
        +-> Storage -------------------------- +
        +-> Infrastructure -------------------+
```

| Project | Holds | Must not reference |
|---|---|---|
| `MCPal.Server.Domain` | entities, enums, pure rules (`AccessEvaluator` logic types, policy records, `ToolNaming`, `RedirectUriPolicy`, claims helpers, `ApiKeyQueries`, `McpalOptions`), ports (`IMcpalData`, `IDataTransaction`, `IEmailSender`, `IBridgeInvoker`, `IAccessTokenValidator`, `IApiKeyRevocationListener`) | EF Core, ASP.NET Core, any adapter |
| `MCPal.Server.Application` | services (`CompanyService`, `ApiKeyService`, `OAuthService`, `AccessService`, `TeamService`, `AccountMailer`, `SigningKeyStore`, `UserContextIssuer`, `AuditWriter`, `AuditRetention`, `CallRelay`, `ConnectionRegistry`, ...), background services, telemetry, `ApplicationModule` | Npgsql, ASP.NET Core HTTP/SignalR/MVC, Storage, Infrastructure, Web |
| `MCPal.Server.Storage` | `MCPalDbContext` (implements `IMcpalData`), migrations, design-time factory, `DatabaseMigrator`, `StorageModule` | Application, Web |
| `MCPal.Server.Infrastructure` | `SmtpEmailSender` (MailKit), `LogEmailSender`, `InfrastructureModule` | Application, Storage, Web |
| `MCPal.Server.Web` | endpoints, `McpBearerAuthenticationHandler`, `TenantToolHandlers`, `BridgeHub`, `HubBridgeInvoker`, `ServerWebServices`, `WebModule` | Storage, Infrastructure |
| `MCPal.Server` | `Program.cs`, `ServerModule` (composes the modules), Dockerfile, `wwwroot` | n/a |

## Decisions

- Data port: `IMcpalData` exposes `Query<T>()`, `Add`, `AddRange`, `Remove`, `RemoveRange`, `SaveChangesAsync`, `BeginTransactionAsync`. `MCPalDbContext` implements it. Existing LINQ stays unchanged. The Postgres advisory lock is `IDataTransaction.LockAsync`.
- Application references `Microsoft.EntityFrameworkCore` (provider-agnostic) for the async LINQ operators and `ExecuteUpdateAsync`/`ExecuteDeleteAsync`. It does not reference Npgsql.
- Domain references `Microsoft.Extensions.Identity.Stores` because `PortalUser : IdentityUser`.
- Namespaces do not change (`MCPal.Server.Tenancy`, `.Access`, `.Storage`, ...). Types stay `internal`; sibling projects see them through `InternalsVisibleTo`.
- Out of scope: five-file entity pattern, per-aggregate repositories, splitting the test project.

## Verification

- Baseline and final: `dotnet build MCPal.slnx` (0 warnings), `dotnet test MCPal.slnx` (Bridge 116, Server 437, E2E 17 per `plan.md`).
- Architecture test in `MCPal.Server.Tests` that fails when a ring references a forbidden assembly.
- `docker compose build`.

## Order

1. In place: introduce `IMcpalData`, move all services, endpoints and background code off `MCPalDbContext`. Full suite.
2. Extract Domain, Storage, Infrastructure, Application, Web, host. Build after each.
3. Update solution files, Dockerfile, docs.
4. Add the architecture test.

## Deviations from the design as approved

- Ports in Domain are only those that an adapter ring implements: `IMcpalData`, `IDataTransaction`, `IEmailSender` and the new `IAuditSearch`. `IBridgeInvoker`, `IAccessTokenValidator` and `IApiKeyRevocationListener` stay in Application (the Web ring or Application itself implements them, and Web references Application).
- The advisory lock is `IDataTransaction.LockAsync(key)`, not a separate `IAdvisoryLock`.
- `IAuditSearch` (Domain) with `PostgresAuditSearch` (Storage) is new: the audit text filter used Npgsql's `EF.Functions.ILike` inside `AuditEndpoints`, which the Web ring may not reference.
- Storage registers the ASP.NET Identity stores (`IdentityBuilder.AddEntityFrameworkStores`) and the database health check for the web host; `AddIdentity` stays in Web. `HealthTags.Ready` lives in Domain so Storage and the host agree on the tag.
- `AccessGroup.MaxNameLength` moved into the entity so the DbContext configuration does not reference `AccessService`.
- `ServerModule` registers Storage before Application: hosted services start in registration order and the migration must run first. `ArchitectureTests.ServerModule_HostedServices_StartMigrationFirst` locks this.
- `server/Directory.Build.props` holds the `InternalsVisibleTo` list for all rings, so the csproj files carry none.
