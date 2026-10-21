# v2 B5b1 — framework-level audit of rejected mutations

Status: B5b1 independently accepted on 2026-10-20 (America/Vancouver). Final prototype automated acceptance is recorded in `docs/v2-prototype-acceptance.zh-CN.md`: backend 545 passed, real business/browser 62 passed, real Kestrel rejection audit 7 passed. Build zero warnings/errors; V2 whitespace verified. All data/profiles were temporary; legacy database/accounts unchanged.

## What is logged

A v2 **mutation** endpoint on the allowlist (every POST/PUT/DELETE command of the v2 controllers, mapped to its `ActivityTypes` value) that is rejected by the framework is appended once to the immutable `ActivityLogs`:

| Status | Rejection site (explicit request-scoped mark) | `FailureCode` |
| --- | --- | --- |
| 400 | model-state factory: malformed JSON, missing body, unbindable fields | `VALIDATION_FAILED` |
| 415 | MVC content-type check (no hook; recognized by status, which no v2 use case returns) | `VALIDATION_FAILED` (same as the response) |
| 401 | authentication challenge | `UNAUTHENTICATED` / `SESSION_EXPIRED` / `SESSION_REVOKED` |
| 403 | authorization forbid for the current role | `FORBIDDEN` |
| 429 | rate limiter `OnRejected` (`Retry-After` unchanged) | `RATE_LIMITED` |
| 413 | declared `Content-Length` check, and the exception handler for the server's own chunked-body limit | `PAYLOAD_TOO_LARGE` |

Each entry has:

- `Success = 0`, the description `请求被拒绝` and a fixed Chinese reason per status;
- a subject only when the route `{id}` is a valid positive ID (type from the allowlist);
- an actor only as validated against the database in this request: none for 401 (also revoked/expired tokens), none for a 413 rejected before authentication, otherwise the current session's account and role.

Nothing else is written: no body, query, raw path, header, cookie, token, password, exception or model-binding text.

## No duplicates

The mark is set only at the framework sites above. Use-case failures (`V2CatalogWriter`, `V2AuthService`: validation, business rules, login failures, the writer's own 401/403 re-check) log themselves and never set it. The middleware also requires the final status to equal the marked status. Reads, health checks and unknown routes are never audited.

## Safety

- `V2RejectionAuditMiddleware` runs outside the exception handler and every rejection site, after the response has been produced. It captures the endpoint before the pipeline runs, because the exception handler clears it.
- The write uses `IV2IdentityStore.AppendActivityAsync` (the writer queue), with its own 5 s cancellation token, not RequestAborted. This permits auditing an aborted client only if the marked rejection pipeline still completes; it does not guarantee a business log for every disconnected request.
- The 5 s bound is cooperative, not a hard deadline. The token is only observed at asynchronous points. A synchronous SQLite busy wait (the connection's busy timeout) or a slow commit can run past it, and the request then finishes later.
- Any storage failure is only logged by the host (exception type name only) and never changes the response. There is no recursion, because the audit is not an HTTP request.

## Limitations

- **Pre-pipeline rejections are not seen.** If Kestrel rejects a connection or request before the ASP.NET pipeline runs (malformed HTTP, header limits, a connection closed by a reverse proxy or proxy body limit), no entry is written.
- **Kestrel chunked bodies:** the limit enforced while MVC reads the body reaches the exception handler and is audited. Both declared and chunked paths passed `node tools/v2-framework-audit-e2e.mjs` against actual Kestrel; in-memory API tests alone do not prove the chunked path.
- **The 413 actor.** The declared-length 413 happens before authentication, so it never names an actor.
- **Rate-limited reads.** A 429 is audited only for mutations. Rate-limited reads are not logged.
- **The audit entry adds latency.** It is written before the request finishes, so it adds the time of one log insert after the response body.

## Tests

`tests/Wms.Api.Tests/V2/V2RejectionAuditApiTests.cs`: HTTP requests, then checks on the SQLite rows of the temporary v2 file.

- Each rejection is logged exactly once.
- Use-case and login failures are not duplicated.
- Revoked tokens leave no actor.
- No secret appears in the log, and the log is immutable.
- 429 is covered with a lowered permit limit.
- A wrapping `IV2IdentityStore` (test services only) makes every audit append fail. The original 401 and 429 status, problem body and `Retry-After` stay intact, and each append receives its own cancellable token, never the request's `RequestAborted`.

```
dotnet test tests/Wms.Api.Tests --filter FullyQualifiedName~V2RejectionAuditApiTests
```
