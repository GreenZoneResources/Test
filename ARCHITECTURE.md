# Statement Generation Portal — Architecture & Setup

Backend implementation of the 10 epics / 18 user stories in *Statement
Generation Portal Enhancement — User Stories & Acceptance Criteria*. Frontend
is out of scope for this pass (API-only, per project decision); a SPA can be
added later against the contract below without changing this backend.

## 1. Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 9 / ASP.NET Core Web API | Matches the existing LotusBank stack (Scalar, `AddOpenApi`, Serilog). |
| Auth | Existing LotusBank SSO (delegated), cookie-based local session | Reuses AD/Graph integration that already exists; portal never re-implements AD auth. |
| Session store | Redis (`IDistributedCache` + custom `ITicketStore`) | Needed for *true* server-side session revocation (US-02, US-18) — see §3. |
| Database | SQL Server / EF Core | Matches existing infra; audit table needs relational filtering (US-14). |
| Downstream statement engine | Existing Statement Service, called over HTTP | Per project decision — treated as an external REST dependency, wrapped by an adapter interface. |
| Resilience | `Microsoft.Extensions.Http.Resilience` (standard handler: retry + circuit breaker + timeout) | SSO and the Statement Service are both external dependencies on the request's critical path. |
| Logging | Serilog → Console + Seq | Structured, queryable logs; separate from the audit trail (below). |

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
    Common/ICurrentUserContext.cs  identity resolved from the session, never from request input
    Common/AccountNumberMasker.cs  masks at the point of use (US-11)
    Audit/IAuditWriter.cs          append-only
    Audit/AuditDtos.cs             IAuditQueryService — read-only
    Statements/                    DTOs, FluentValidation rules, IStatementServiceClient (adapter interface)
    Permissions/IPermissionService.cs

  StatementPortal.Infrastructure/  EF Core, HTTP clients, Redis, SSO integration
    Persistence/                   StatementPortalDbContext, AuditRepository (implements both audit interfaces)
    Sso/                           SsoClient (OAuth2 code+PKCE against existing SSO), PermissionService (cached)
    StatementService/              StatementServiceClient (typed HttpClient, resilience-wrapped)
    Auth/                          DistributedCacheTicketStore, CurrentUserContext

  StatementPortal.Api/             composition root
    Program.cs                    auth, authorization policies, rate limiting, security headers, CORS
    Controllers/                  AuthController, StatementsController, AuditController
    Authorization/                 permission-claim policy handler
    Middleware/                    security headers, global exception handling
```

Dependency direction is strictly inward: `Api → Infrastructure → Application →
Domain`. Nothing in `Application` or `Domain` references EF Core, HTTP, or
ASP.NET Core — that keeps the business rules (validation, masking, audit
shape) testable without spinning up a database, and keeps the Statement
Service/SSO integration swappable behind their interfaces.

## 3. Authentication & session design (Epic 1, Epic 10)

The portal does **not** talk to Active Directory. It delegates to the
existing SSO service and only consumes the result — this is a
**Backend-for-Frontend (BFF)** pattern:

```
Browser                StatementPortal.Api              LotusBank SSO
   |  GET /api/auth/login    |                                |
   |------------------------>|                                |
   |   302 → SSO authorize   |  (state + PKCE challenge set,   |
   |<------------------------|   verifier kept server-side)    |
   |------------------------------------------------------------------>|
   |                         |         staff authenticates via AD      |
   |<------------------------------------------------------------------|
   |  302 → /api/auth/callback?code=...&state=...                      |
   |------------------------>|                                |
   |                         |  POST code+verifier (server-to-server)  |
   |                         |----------------------------------------->|
   |                         |<-----------------------------------------|
   |                         |  validate JWT (issuer/audience/JWKS)     |
   |                         |  fetch permissions, build ClaimsPrincipal|
   |  Set-Cookie: __Host-StatementPortal.Session (HttpOnly, Secure,     |
   |  SameSite=Strict) — opaque key into Redis-backed ticket store      |
   |<------------------------|                                |
```

Why this matters for the acceptance criteria specifically:

- **US-01** (AD auth) is satisfied by SSO itself; the portal's job is to
  reject anyone SSO doesn't vouch for, and separately reject anyone SSO
  vouches for but who has zero portal permissions ("unauthorized staff
  access" — `AuthController.Callback` returns `403` when `permissions.Count
  == 0`).
- **US-02** ("cookie should not expose authentication information to
  client-side JavaScript") — the SSO-issued token is exchanged and validated
  entirely server-side inside `SsoClient`; it never appears in a response the
  browser can read. The browser only ever receives `__Host-*` cookie:
  `HttpOnly`, `Secure`, `SameSite=Strict`.
- **US-02 session expiry / US-18 logout** — plain ASP.NET Core cookie auth is
  self-contained (the ticket lives inside the cookie), so a server can't
  truly revoke a session before its cryptographic expiry. `DistributedCacheTicketStore`
  (an `ITicketStore` backed by Redis) makes the cookie an opaque lookup key
  instead: `SignOutAsync` deletes the Redis entry immediately, so the very
  next request with that cookie is rejected — not just eventually, but on the
  next request.
- PKCE (RFC 7636) on the code exchange closes the classic
  authorization-code-interception gap.

## 4. RBAC (Epic 2)

Permissions come back from SSO/entitlements as a string list per staff ID,
cached 5 minutes (`PermissionService`), and are baked into the session's
`ClaimsPrincipal` as repeated `permission` claims at sign-in time. Enforcement
is **policy-based**, not role-string-based, so a new permission is one enum
value + one policy registration:

```csharp
[Authorize(Policy = PortalPolicies.SingleStatement)]
```

`PermissionAuthorizationHandler` is the single place a permission decision is
made. The doc's explicit callout —

> Frontend visibility must not be treated as the authorization mechanism.

— is why `GET /api/auth/me` (module visibility for the landing page) and the
real enforcement on `POST /api/statements/single` are two different code
paths: `me` only tells the UI what to *render*; every state-changing endpoint
re-checks the policy independently, so a modified/replayed request from an
unprivileged session is rejected regardless of what the UI showed.

The global `FallbackPolicy` requires authentication for *every* endpoint by
default (US-17); only `login`/`callback` are `[AllowAnonymous]`.

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

- **Deny-by-default authorization** — global `FallbackPolicy` requires an
  authenticated session; nothing is reachable anonymously except the two auth
  endpoints.
- **IP capture integrity** — `ForwardedHeadersOptions.KnownProxies` is
  populated explicitly from config; `X-Forwarded-For` is trusted only from
  that list, so a client can't spoof the IP address that lands in the audit
  trail by sending its own `X-Forwarded-For` header.
- **Rate limiting** — fixed-window limiters on `/api/auth/*` (by IP, blunts
  credential-stuffing against the login/callback flow) and
  `/api/statements/*` (by staff ID once authenticated, blunts scripted abuse
  of statement generation).
- **Security headers** — `SecurityHeadersMiddleware` strips `Server`/
  `X-Powered-By`, sets `X-Content-Type-Options`, `X-Frame-Options: DENY`,
  a restrictive `Content-Security-Policy` (this is a pure JSON API, so
  `default-src 'none'` is intentional), and `Cache-Control: no-store` since
  every response here is either statement or audit data.
- **No leaky errors** — `ExceptionHandlingMiddleware` returns a generic
  message on unhandled exceptions; details go to Serilog only, never to the
  response body.
- **CORS** — explicit origin allow-list (`AllowedOrigins` config), credentials
  enabled only for those origins, method allow-list (`GET`/`POST`).
- **Resilience** — SSO and Statement Service calls go through
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
live from SSO and cached in memory, not persisted locally, so there is
nothing here to keep in sync with SSO's own user store.

## 9. API surface

| Method | Route | Policy | Story |
|---|---|---|---|
| GET | `/api/auth/login` | anonymous | US-01 |
| GET | `/api/auth/callback` | anonymous | US-01 |
| POST | `/api/auth/logout` | authenticated | US-18 |
| GET | `/api/auth/me` | authenticated | US-03/US-04 |
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
dotnet user-secrets set "Sso:ClientSecret" "<value>"
dotnet user-secrets set "StatementService:ApiKey" "<value>"
dotnet user-secrets set "Seq:ApiKey" "<value>"          # optional

# 3. Dependencies
docker run -d -p 6379:6379 redis:7
docker run -d -e ACCEPT_EULA=Y -p 5341:5341 -p 8080:8080 datalust/seq:latest

# 4. Database
dotnet ef migrations add InitialCreate -p ../StatementPortal.Infrastructure -s .
dotnet ef database update -p ../StatementPortal.Infrastructure -s .
# then apply the INSERT/SELECT-only GRANT for the app's SQL login — see §6.

# 5. Run
dotnet run
```

## 11. Integration points intentionally left open

Two external contracts weren't available to shape exactly, so both are
isolated behind a single interface each — adjust the implementation, not the
callers, once the real contract is confirmed:

- **`ISsoClient`** (`Infrastructure/Sso/SsoClient.cs`) assumes a standard
  OAuth2 authorization-code + PKCE flow with a JWKS endpoint for signature
  validation. If the existing SSO exposes something else (e.g. a bespoke
  token format, a different claim naming scheme), only this file changes.
- **`IStatementServiceClient`** (`Infrastructure/StatementService/StatementServiceClient.cs`)
  assumes `POST api/statements/single` / `api/statements/bulk` with a
  JSON body and an `X-Request-Id` header. Adjust to the real Statement
  Service's contract the same way.

Everything upstream of those two classes (validation, RBAC, audit, the
controllers) is independent of those specifics and shouldn't need to change
when the real contracts are confirmed.
