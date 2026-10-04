# Easier bridge setup: enrollment codes

Date: 2026-10-04. Status: draft, waiting for review.

## Goal

An owner goes from the portal's Setup page to a running bridge with one copy-paste command. The owner does not copy a bridge key or the server URL by hand, and the key never appears in a shell command or in shell history.

Today the Setup page creates a bridge key, shows it once and embeds it in the `docker run` or install command. That works, but the key is a long-lived secret that travels through the clipboard, the terminal and shell history, and it must be copied together with the URL.

## Scope

In scope:
- Single-use enrollment codes that a bridge redeems for its own bridge key.
- A bridge credentials file, a `mcpal-bridge enroll` verb and automatic enrollment on `run`.
- `--enroll` / `-Enroll` for the Linux and Windows install scripts.
- The Setup page generates the enroll command; the manual key flow stays as an advanced option.
- Documentation polish: shorter README quickstart, a troubleshooting table, next steps in the installer output.

Out of scope (separate work, see `docs/next-steps.md` if wanted later):
- Helping with `mcp.json` (paste-and-lint box, secret extraction, better `check` messages).
- Reporting local server failures from the bridge to the portal (protocol 1.3).
- Storing server configuration in the portal. Secrets of local servers must stay on-prem.
- The claude.ai connector steps, SSO.

## Design

### Server

Entity `BridgeEnrollment` (Domain, `Tenancy/Entities.cs`; EF migration `BridgeEnrollments`):

| Field | Notes |
|---|---|
| `Id` | Guid |
| `CompanyId` | the company the new key belongs to |
| `CodeHash` | SHA-256 hex of the code (same `ApiKeyService.Hash`); unique index. The code is never stored. |
| `CreatedByUserId` | owner who created it |
| `CreatedAt`, `ExpiresAt` | lifetime 15 minutes (`Mcpal:EnrollmentLifetimeMinutes`, default 15) |
| `RedeemedAt` | null until used |
| `ApiKeyId` | the key created on redemption |

Code format: `mcpale_` + 24 random characters of the existing key alphabet (about 143 bits). The code carries no company information; the server finds the company through the hash.

Service `BridgeEnrollmentService` (Application, registered in `ApplicationModule`):
- `CreateAsync(companyId, userId)` creates a code. At most 10 unredeemed, unexpired codes per company; the 11th request fails with a clear message. Expired rows are deleted when a new code is created.
- `RedeemAsync(code, bridgeName)` redeems atomically in one transaction:
  1. `ExecuteUpdateAsync` on the row where `CodeHash` matches, `RedeemedAt IS NULL` and `ExpiresAt > now`, setting `RedeemedAt`. One affected row means success; zero means invalid, used or expired (the caller cannot tell which).
  2. Create the key with `ApiKeyService.CreateAsync(NewApiKey.Bridge("Bridge <name>", createdByUserId))`, store its id in `ApiKeyId`.
  3. The company must be enabled and the creating user must still be active; otherwise the code is invalid.
- Time only from `TimeProvider`; every method takes a `CancellationToken`.

Endpoints (Web):
- `POST /api/portal/setup/enrollments`: owner only (`OwnerOnlyFilter`, anti-forgery like the other portal POSTs). Returns `{ code, expiresAt }`. The code is shown once.
- `POST /api/bridge/enroll`: anonymous, with a rate-limit policy per remote IP (the existing `AddRateLimiter` setup in `ServerWebServices`; the policy limits guessing, which is already hopeless at 143 bits, and creation spam). Body `{ code, bridgeName }`; response `{ url, apiKey }` where `url` is `Mcpal:PublicUrl`. Every failure answers `400 { error: "invalid_code" }`. The path starts with `/api`, so the SPA fallback does not capture it.
- `GET /api/portal/setup` stays; its response is unchanged. The enroll command is built in the SPA from `McpalUrl` and the new code.

The bridge key created by enrollment is an ordinary bridge key: it appears on the API keys page (name "Bridge `<hostname>`", creator shown), can be revoked there, and revoking it closes the tunnel through the existing `TunnelSweeper`.

Tenant isolation: the code is bound to one company at creation. Redemption creates the key for that company only. Tests cover a code of company A never producing a key for company B.

### Bridge

- Credentials file `credentials.json` holds `{ "apiKey": "mcpal_…" }`, written with mode 600 (Linux, Docker) or an ACL for Administrators, SYSTEM and the service account (Windows, same helper idea as `install.ps1`). Location: `mcpal.credentialsFile` (relative to `mcpal.json`), default `credentials.json` next to `mcpal.json`; `mcpal.docker.json` sets `/data/credentials.json`.
- Key precedence in `BridgeConfigLoader`: `MCPAL_API_KEY`, then `mcpal.apiKey`, then the credentials file. A missing key is still a configuration error, with an updated message that names `MCPAL_ENROLL`.
- Verb `enroll`: `mcpal-bridge enroll --url <server> --code <code> [--config path]`. Posts to `/api/bridge/enroll` with `bridgeName` (config `mcpal.bridgeName`, else the host name), writes the credentials file, prints the result and exits 0. On `invalid_code` it prints "The enrollment code is invalid, expired or already used. Create a new one on the Setup page." and exits 1. The URL comes from `--url` or `mcpal.url`/`MCPAL_URL`. The code comes from `--code` or `MCPAL_ENROLL`.
- Auto-enroll on `run`: when no key is found and `MCPAL_ENROLL` is set, the bridge enrolls first, saves the credentials file and continues. When a key exists, `MCPAL_ENROLL` is ignored (the code was used already; a restarted container must not fail). If the volume with the credentials file is lost, the code is spent and the bridge stops with the message above.
- A `RedeemedAt` code cannot be reused, so a retry after a network failure between the server's commit and the bridge's write would lose the key. Accepted: the owner revokes the orphan key and makes a new code. The bridge writes the file before printing success.
- Protocol: no change. Enrollment is plain HTTPS before the tunnel exists.

### Install scripts and Docker

- `install.sh --enroll <code>` and `install.ps1 -Enroll <code>`: after installing the binary and config, run `mcpal-bridge enroll` as the service user (`runuser -u mcpal` on Linux), so the credentials file has the right owner. `--api-key` stays. Both together is an error. The existing "start only when `check` passes" rule is unchanged.
- Docker: `-e MCPAL_URL=… -e MCPAL_ENROLL=<code> -v mcpal-bridge-data:/data` (no `MCPAL_API_KEY`). `compose.yml` accepts `MCPAL_ENROLL` as an alternative to `MCPAL_API_KEY`. `HEALTHCHECK` and the contract table in `bridge/packaging/docker/README.md` are updated.

### Portal Setup page

- Step "Create the bridge key" becomes "Generate the command". One button creates a code; the page shows the command for the chosen method (Docker, Linux, Windows) with URL and code filled in, a copy button and a countdown to expiry ("expires in 14:32"). After expiry the command is greyed out and the button reads "Generate a new code".
- The old manual key flow (create a bridge key, copy it) remains below as "Use a key instead (advanced)", unchanged, so nothing is removed for people who need it (for example to preconfigure many bridges with one key).
- The "bridge is online" check and the sample `mcp.json` step stay as they are.
- Strings go through the typed `t()` helper in `src/i18n`.

### Documentation polish

- README quickstart step 3 shows the enroll command first and the key-based commands as "alternatively".
- New README troubleshooting table: bridge shows offline (check URL, outbound HTTPS, `docker logs`), `invalid_code` (expired, used, new code), `check` fails (see the log line of the server).
- Installer scripts print the next steps (where `mcp.json` is, how to follow the log, that the portal shows the bridge online).
- `docs/tunnel-protocol.md`: short section on enrollment (it is outside the tunnel). `docs/next-steps.md`: list the out-of-scope ideas above as backlog. `CHANGELOG.md` (Unreleased, Added): enrollment codes, new option `Mcpal__EnrollmentLifetimeMinutes`, new migration, new bridge verb and `credentialsFile`. Nothing is breaking.

## Error handling

- Server: all redemption failures return the same `invalid_code`; details only in server logs (counted in a `ServerTelemetry` counter by outcome: `redeemed`, `rejected`).
- Too many open codes: `409` from the create endpoint with a message the SPA shows.
- Bridge: network or TLS errors during enrollment exit 1 with the underlying message and the URL used. A credentials file that is unreadable or invalid JSON is a configuration error naming the path.

## Testing

- Server (NUnit, `ServerTestBase`): create and redeem succeed; second redeem fails; expired code fails (`FakeTimeProvider`); two concurrent redeems produce exactly one key; code of company A yields a key of company A and cannot be used with another company's data; disabled company or inactive creator invalidates the code; 11th open code is refused; expired rows are cleaned up; created key is a bridge key with `CreatedByUserId`.
- Server web (`ServerWebApplicationFactory`): both endpoints, owner-only create (member gets 403), anonymous redeem, rate limit, anti-forgery on create, `/api/bridge/enroll` not swallowed by the SPA fallback.
- Bridge: key precedence (env, file value, credentials file); `credentialsFile` path resolution; `enroll` writes the file and sets the mode; auto-enroll on `run` only without a key; `invalid_code` message and exit code; unreadable credentials file.
- E2E (`tests/MCPal.E2E.Tests`): create a code through the portal API, start a real bridge with only `MCPAL_URL` and `MCPAL_ENROLL`, see it connect, call a tool.
- SPA (vitest): Setup page generates the command, shows the countdown, handles expiry and the 409; the advanced key flow still works.
- Scripts: `test-install.sh` covers `--enroll` against a stub (`MCPAL_SKIP_SERVICE=1` style); `test-image.sh` keeps running `check`.
- Verification before completion: `dotnet build MCPal.slnx` (0 warnings), `dotnet test MCPal.slnx`, `npm run typecheck`, `npm test`.

## Open points

- None known. The code lifetime default (15 minutes) and the limit of 10 open codes are defaults that can be tuned in options without a design change.
