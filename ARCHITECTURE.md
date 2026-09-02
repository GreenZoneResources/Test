# Statement Generation Portal — Architecture & Setup

Backend implementation of the 10 epics / 18 user stories in *Statement
Generation Portal Enhancement — User Stories & Acceptance Criteria*. Frontend
is out of scope for this pass (API-only, per project decision); a SPA can be
added later against the contract below without changing this backend.

## 1. Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 9 / ASP.NET Core Web API | Matches the existing LotusBank stack (Swagger, Serilog). |
| Auth | Local JWT Bearer validation — this service **is** the authority on who/what for its own endpoints | See §3: it validates SSO-issued tokens itself rather than calling back to SSO at request time. |
| Database | SQL Server / EF Core | Matches existing infra; audit table needs relational filtering (US-14). Also the same DB SSO's `GetTokenSettings` reads, for the shared signing key. |
| Downstream statement engine | Existing Statement Service, called over HTTP | Per project decision — treated as an external REST dependency, wrapped by an adapter interface. Requires its own service-account login (§5). |
| Resilience | `Microsoft.Extensions.Http.Resilience` (standard handler: retry + circuit breaker + timeout) | The Statement Service is an external dependency on the request's critical path. |
| API docs | Swagger (Swashbuckle) with a Bearer security scheme | Lets a token be pasted into Swagger UI directly for testing protected endpoints. |
| Logging | Serilog → Console + Seq | Structured, queryable logs; separate from the audit trail (below). Every auth failure (401/403) logs through `ILogger`, not `Console.WriteLine`, specifically so it's visible in Seq. |

**Audit trail vs. application logs — deliberately two different systems.**
Serilog/Seq is for operational diagnostics (exceptions, latency, request
tracing). The audit trail (Epics 6–8) is a compliance record: it lives in its
own SQL table with its own access controls, is never pruned/rotated the way
logs are, and its write path is described in §4. Do not point audit writes at
Seq — Seq is not access-controlled the way the audit table is, and mixing the
two would violate US-16's protection requirement.

## 2. Solution layout (Clean Architecture)

```
StatementPortal.sln
src/
  StatementPortal.Domain/          entities & enums only — no framework dependencies
    Enums/ModulePermission.cs      SingleStatement | BulkStatement | Audit | AuditAdmin
    Enums/AuditActivityStatus.cs   Success | Failed | Denied
    Entities/AuditRecord.cs        immutable: constructor-only, no Update/Delete methods

  StatementPortal.Application/     use cases, interfaces, validation — no EF/HTTP here
    Common/ICurrentUserContext.cs  identity resolved from the validated token, never from request input
    Common/AccountNumberMasker.cs  masks at the point of use (US-11)
    Audit/IAuditWriter.cs          append-only
    Audit/AuditDtos.cs             IAuditQueryService — read-only
    Statements/                    DTOs, FluentValidation rules, IStatementServiceClient (adapter interface)

  StatementPortal.Infrastructure/  EF Core, HTTP clients, auth
    Persistence/                   StatementPortalDbContext, AuditRepository (implements both audit interfaces)
    Auth/                          JwtSigningKeyProvider, AppRolesClaimsMiddleware, AuthenticationExtensions, CurrentUserContext
    StatementService/              StatementServiceClient, StatementServiceAuthenticator + AuthHandler (service-account login)

  StatementPortal.Api/             composition root
    Program.cs                    auth, authorization policies, rate limiting, security headers, CORS, Swagger
    Controllers/                  MeController, StatementsController, AuditController
    Authorization/                 permission-claim policy handler
    Middleware/                    security headers, global exception handling
```

Dependency direction is strictly inward: `Api → Infrastructure → Application →
Domain`. Nothing in `Application` or `Domain` references EF Core, HTTP, or
ASP.NET Core — that keeps the business rules (validation, masking, audit
shape) testable without spinning up a database, and keeps the Statement
Service integration swappable behind its interface.

## 3. Authentication design (Epic 1, Epic 10) — local JWT validation, no SSO call at request time

This service does **not** talk to Active Directory, and — unlike the
project's earlier BFF design — it also does **not** call out to SSO over the
network to authenticate a request. It is a pure **resource server**: SSO
already authenticated the staff member and issued a JWT (the `CustomJwt`
scheme used across the LotusBank services, HS256-signed with a key shared via
the same `[dbo].[GetTokenSettings]` stored procedure SSO itself reads); this
service validates that token **locally**, using the same signing key, issuer,
and audience. There is no login, callback, or logout endpoint here at all —
those concerns belong entirely to SSO.

```
Browser/Frontend          StatementPortal.Api
      |  request + Authorization: Bearer <token>   |
      |  (or the "access-token" cookie SSO set)     |
      |--------------------------------------------->|
      |                    JwtBearer validates the token locally:
      |                    issuer/audience/signature/expiry
      |                    (signing key cached after first DB load
      |                     — see JwtSigningKeyProvider)
      |                    AppRolesClaimsMiddleware extracts this
      |                    app's roles from the token's "appRoles"
      |                    claim → permission claims
      |                    PermissionAuthorizationHandler enforces
      |                    the endpoint's required policy
      |<---------------------------------------------|
```

This directly resolves a real production issue hit while building this
service: the previous design called SSO's `/auth-me` endpoint over HTTPS on
every request, and that outbound call was failing with TLS handshake errors
in the deployed environment. Validating the token locally removes that call
entirely — there is nothing left to fail on that network path.

- **`JwtSigningKeyProvider`** loads the issuer/audience/key from the database
  the first time a token needs validating, then caches it for the rest of the
  process's lifetime (double-checked locking via `SemaphoreSlim`). This is
  deliberately *not* loaded via `builder.Services.BuildServiceProvider()...Result`
  at host-startup time — that pattern blocks the entire app from starting on a
  synchronous DB round trip and spins up a throwaway second DI container just
  to fetch one value. Here, a DB outage delays the *first* authenticated
  request, not the whole app's ability to start.
- **`AppRolesClaimsMiddleware`** runs right after `UseAuthentication()`. SSO's
  `appRoles` claim lists roles across *every* application the staff member has
  access to, not just this one — the middleware filters to the entry whose
  `ApplicationName` matches `Authentication:ApplicationName` (config) before
  turning those roles into this app's own `permission` claims. Flattening
  every app's roles together, as a naive port of SSO's own role-extraction
  code would do, would let a role granted in a completely unrelated
  application leak into this service's authorization decisions.
- **Seq visibility (US-10-adjacent, operational)** — `OnAuthenticationFailed`,
  `OnChallenge`, and `OnForbidden` all log through `ILogger`, not
  `Console.WriteLine`, with the request path and reason. Combined with
  `UseSerilogRequestLogging()`, every 401/403 on any endpoint is queryable in
  Seq without needing to reproduce it.
- **US-01** ("unauthorized staff access") is satisfied by
  `AppRolesClaimsMiddleware` returning `403` when the token has no `appRoles`
  entry for this application at all.

## 4. RBAC (Epic 2)

Permissions live entirely inside the validated JWT (`appRoles` claim,
filtered to this application by `AppRolesClaimsMiddleware`) — there is no
separate permissions lookup, cache, or network call. Enforcement is
**policy-based**, not role-string-based, so a new permission is one enum
value + one policy registration:

```csharp
[Authorize(Policy = PortalPolicies.SingleStatement)]
```

`PermissionAuthorizationHandler` is the single place a permission decision is
made. The doc's explicit callout —

> Frontend visibility must not be treated as the authorization mechanism.

— is why `GET /api/me` (module visibility for the landing page) and the real
enforcement on `POST /api/statements/single` are two different code paths:
`me` only tells the UI what to *render*; every state-changing endpoint
re-checks the policy independently against the token's own claims, so a
modified/replayed request is rejected regardless of what the UI showed.

The global `FallbackPolicy` requires a validated bearer token for *every*
endpoint by default (US-17) — nothing in this service is anonymous.

## 5. Statement generation (Epics 3–5)

`StatementsController` → `IStatementRequestService` → `IStatementServiceClient`.

- The **Request ID is generated server-side** (`Guid.NewGuid()` in
  `StatementRequestService`), never accepted from the client — this is what
  US-06/US-08 mean by "the backend should generate or assign a Request ID."
- FluentValidation runs before the Statement Service is ever called; a
  validation failure short-circuits with an audit entry (`Failed`) and the
  downstream service is never invoked — directly satisfying the "Statement
  Service should not be invoked" scenarios in US-05/US-06/US-07.
- `IStatementServiceClient` is the *only* thing in the codebase that knows the
  Statement Service's URL. There is no route, controller, or config value
  that would let a frontend call it directly — US-09's "prevent the frontend
  from directly invoking the Statement Service" is enforced structurally
  (the service isn't reachable from outside the backend's network in the
  first place) and architecturally (nothing in this codebase exposes it).
- Bulk requests are capped (500 items/request) so one submission can't be
  used to exhaust the downstream service or this API's own thread pool.
- The Statement Service itself requires a service-account login (its own
  username/password, unrelated to staff SSO) before it accepts any call.
  That login is enforced transport-level, not by controller/service code:
  `StatementServiceAuthHandler`, a `DelegatingHandler` registered on
  `IStatementServiceClient`'s `HttpClient`, attaches a valid bearer token to
  every outgoing request automatically. `StatementServiceAuthenticator`
  (singleton — its cached token and refresh lock must be shared process-wide)
  performs the actual login only when the cached token is missing or close to
  expiry, so this doesn't add a login round-trip to every statement request.
  There is no code path from `StatementsController` to the Statement Service
  that can skip this — it isn't a check anything can forget to call.

## 6. Audit trail (Epics 6–8)

**Write path.** `StatementRequestService` writes exactly one `AuditEntry` per
request, after the outcome is known (`Success`/`Failed`), plus one for a
validation rejection (`Failed`) — matching "the record should contain the
activity status." Account numbers are masked *before* the entry is
constructed (`AccountNumberMasker.Mask`, keeps last 4 digits), so a plaintext
account number never exists in the audit store to begin with — safer than
masking only at display time.

**Immutability (US-16).** Enforced at three levels, not just one:

1. **Type-level** — `AuditRecord`'s properties are `private set`; the only
   public entry point is its constructor. There is no `Update` method to call.
2. **Interface-level** — `IAuditWriter` has one method, `RecordAsync`.
   `IAuditQueryService` has two, both reads. Neither interface has an Update
   or Delete signature anywhere in the codebase.
3. **Database-level (operational step, not yet scripted here)** — grant the
   application's SQL login `INSERT, SELECT` only on `AuditRecords`; do **not**
   grant `UPDATE`/`DELETE`. This is defense in depth: even a future code
   defect, or a compromised app-tier credential, cannot alter history because
   the database itself refuses the statement. Add this `GRANT`/`DENY` pair to
   your deployment script when you provision the database login.

**Read path.** `AuditController` is `GET`-only — no `PUT`/`PATCH`/`DELETE`
action exists on it, matching level 2 above at the API surface too. `US-14`'s
combined filters (date range, staff, branch, module, activity, status) are
ANDed in `AuditRepository.SearchAsync`; page size is capped at 100 server-side
regardless of what a caller requests.

## 7. Cross-cutting security (Epic 9 + general hardening)

- **Deny-by-default authorization** — global `FallbackPolicy` requires a
  validated bearer token; nothing in this service is anonymous.
- **IP capture integrity** — `ForwardedHeadersOptions.KnownProxies` is
  populated explicitly from config; `X-Forwarded-For` is trusted only from
  that list, so a client can't spoof the IP address that lands in the audit
  trail by sending its own `X-Forwarded-For` header.
- **Rate limiting** — a fixed-window limiter on `/api/statements/*` (by staff
  ID from the validated token) blunts scripted abuse of statement generation.
- **Security headers** — `SecurityHeadersMiddleware` strips `Server`/
  `X-Powered-By`, sets `X-Content-Type-Options`, `X-Frame-Options: DENY`,
  a restrictive `Content-Security-Policy` (this is a pure JSON API, so
  `default-src 'none'` is intentional), and `Cache-Control: no-store` since
  every response here is either statement or audit data.
- **No leaky errors** — `ExceptionHandlingMiddleware` returns a generic
  message on unhandled exceptions; details go to Serilog only, never to the
  response body.
- **CORS** — explicit origin allow-list (`AllowedOrigins` config), credentials
  enabled only for those origins (needed for the SSO-issued `access-token`
  cookie), method allow-list (`GET`/`POST`).
- **Resilience** — Statement Service calls go through
  `AddStandardResilienceHandler()` (retry with jitter, circuit breaker,
  timeout), so a slow/flaky downstream degrades instead of cascading into
  thread-pool exhaustion on the portal.

## 8. Data model

Single table for this backend slice:

```
AuditRecords
  Id                   bigint       PK, identity
  RequestId            uniqueidentifier
  StaffId              nvarchar(64)
  StaffName            nvarchar(256)
  Email                nvarchar(256)
  Branch               nvarchar(128)  null
  Module               nvarchar(64)     SingleStatement | BulkStatement
  Activity             nvarchar(128)
  MaskedAccountNumber  nvarchar(32)   null
  StatementStartDate   date           null
  StatementEndDate     date           null
  Status               nvarchar(16)     Success | Failed | Denied
  FailureReason        nvarchar(1024) null
  IpAddress            nvarchar(64)
  OccurredAtUtc        datetimeoffset

  index (RequestId), (OccurredAtUtc), (StaffId), (Module), (Status)
```

No `Permissions`/`Users` table exists in this database — RBAC is resolved
directly from the validated token's own `appRoles` claim, not persisted
locally, so there is nothing here to keep in sync with SSO's own user store.

## 9. API surface

This service has no login/callback/logout endpoints — SSO owns the entire
authentication lifecycle; every route below requires a bearer token SSO
already issued.

| Method | Route | Policy | Story |
|---|---|---|---|
| GET | `/api/me` | authenticated | US-03/US-04 |
| POST | `/api/statements/single` | `SingleStatement` | US-05/US-06 |
| POST | `/api/statements/bulk` | `BulkStatement` | US-07/US-08 |
| GET | `/api/audit` | `Audit` | US-12/US-13/US-14 |
| GET | `/api/audit/{id}` | `Audit` | US-15 |

## 10. Setup

```bash
# 1. Restore
dotnet restore StatementPortal.sln

# 2. Local secrets (never appsettings.json)
cd src/StatementPortal.Api
dotnet user-secrets set "StatementService:Username" "<value>"
dotnet user-secrets set "StatementService:Password" "<value>"
dotnet user-secrets set "Seq:ApiKey" "<value>"          # optional

# 3. Dependencies
docker run -d -e ACCEPT_EULA=Y -p 5341:5341 -p 8080:8080 datalust/seq:latest

# 4. Database
dotnet ef migrations add InitialCreate -p ../StatementPortal.Infrastructure -s .
dotnet ef database update -p ../StatementPortal.Infrastructure -s .
# then apply the INSERT/SELECT-only GRANT for the app's SQL login — see §6.

# 5. Run
dotnet run
```

## 11. Integration points intentionally left open

External contracts weren't available to shape exactly, so each is isolated
behind a single interface/class — adjust the implementation, not the callers,
once the real contract is confirmed:

- **`CurrentUserContext`** (`Infrastructure/Auth/CurrentUserContext.cs`) reads
  `ClaimTypes.NameIdentifier` for staff id (confirmed against SSO's own
  `TokenValidationParameters.NameClaimType`) but guesses at the claim names
  for staff name/branch (`ClaimTypes.Name`, a `"branch"` claim). Confirm
  against a real SSO-issued token and adjust.
- **`AppRolesClaimsMiddleware`** assumes the `appRoles` claim deserializes to
  a JSON array of `{ ApplicationName, Roles[] }` objects, and that this app is
  registered under `ApplicationName: "StatementPortal"` — adjust
  `Authentication:ApplicationName` (config) or the shape in
  `GroupedRoleEntry` if SSO's actual claim differs.
- **`IStatementServiceClient`** (`Infrastructure/StatementService/StatementServiceClient.cs`)
  assumes `POST api/statements/single` / `api/statements/bulk` with a
  JSON body and an `X-Request-Id` header, and `StatementServiceAuthenticator`
  assumes a `POST {AuthPath}` login returning `{ token, expires, tokenType }`.
  Adjust to the real Statement Service's contract the same way.

Everything upstream of those (validation, RBAC, audit, the controllers) is
independent of those specifics and shouldn't need to change when the real
contracts are confirmed.
