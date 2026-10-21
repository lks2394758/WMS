# M3 acceptance (guide §8)

Date: 2026-10-14. Verdict: **PASS** (in-memory provider only; no concurrency claim, AGENTS.md rule 4).

## How it was run

The repository `global.json` pins SDK `10.0.100` with `latestPatch`, which only accepts 10.0.1xx. The machine has 10.0.401, so every `dotnet` command in the repository fails at once (`A compatible .NET SDK was not found`, exit after 0.06 s). The repository `global.json` was not changed. Acceptance ran in an isolated copy, `C:\Projects\WMS\wms-m3-accept`, holding `src`, `tests`, `contracts`, `docs`, `scripts` and the root build files (no `.git`, `.vs`, `bin`, `obj`, `prototypes`, `tools` or `*.user` files), with its own `global.json` selecting 10.0.401. After the fixes, the copy matched the repository except for line endings. Logs and TRX files are in `C:\Projects\WMS\wms-m3-accept\_acc\final`.

The sequence was the same as `scripts/verify.ps1`:

| Step | Result |
| --- | --- |
| `dotnet restore Wms.sln` | exit 0 |
| `dotnet format whitespace Wms.sln --verify-no-changes` | exit 0, after CRLF normalisation (see below) |
| `dotnet build Wms.sln --no-restore --no-incremental` | exit 0, 0 warnings, 0 errors |
| `dotnet test Wms.sln --no-build --blame-hang --blame-hang-timeout 90s` | exit 0 in 6 s |

| Test project | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Wms.Domain.Tests | 60 | 0 | 0 |
| Wms.Application.Tests | 10 | 0 | 0 |
| Wms.ContractTests | 25 | 0 | 0 |
| Wms.Api.Tests | 55 | 0 | 0 |
| **Total** | **150** | **0** | **0** |

## The reported "hang"

The hang could not be reproduced. The first full run of `Wms.Api.Tests` finished in about 4 s, with blame-hang enabled and no hang sequence written. The full solution, including the build, finished in 10 s when its output went straight through a pipe. These were the actual blockers:

1. **The SDK pin.** `dotnet` cannot start in the repository at all (see above).
2. **Two real test failures.** The first run had 41 of 43 API tests passing:
   - `ValidationProblemTests.Missing_body_is_keyed_as_body`: the `errors` keys were `body` and `request`, the C# parameter name from the implicit required check. This was a product defect.
   - `ApiSmokeTests.Receipt_with_a_non_positive_quantity_is_a_field_validation_problem`: the test read the response stream twice, which threw `ObjectDisposedException`. This was a test defect.

After a build, nine MSBuild nodes (`/nodeReuse:true`) and `VBCSCompiler` stay resident. In these runs they did not block the caller. One possible explanation for the earlier symptom is that a tool limit of about 30 s cut off a cold restore and build. That explanation is not proven.

## Fixes and additions

| Item | Change |
| --- | --- |
| Validation | `ValidationProblems.FromModelState` maps errors on the body-bound parameter to `body` |
| Test defect | `ApiSmokeTests.AssertProblemAsync` returns the parsed problem, so the body is read once |
| Request logging | New `Observability/RequestLoggingMiddleware`: one Information entry `RequestCompleted` with `Method`, `RouteTemplate` (or `(unmatched)`), `StatusCode` and `ElapsedMilliseconds`, inside the `CorrelationId` scope. Never logs the path, query string, body, headers or tokens |
| Readiness | New `Health/ReadinessChecks`: `persistence` reads one location through `CatalogQueries`; `jwt-signing` signs and verifies a probe. Both are tagged `ready`. `/health/ready` runs only these checks; `/health/live` runs none |
| Tests | `RequestLoggingTests` (3), `ReadinessTests` (2, one of which replaces `ICatalogReader` with a failing reader: 503 `Unhealthy`, no detail leaked, liveness stays 200), an OpenAPI public-versus-protected security test, and correlation tests for generated, invalid and error-response IDs |
| Docs | `docs/api-conventions.md` (correlation, rate limit, health, logging, `RATE_LIMITED` 429), `docs/architecture.md` (M3 decisions), `contracts/changelog.md`, `README.md` |

The OpenAPI contract did not change: health endpoints are not in the document. The runtime document matches `contracts/openapi/wms.v1.json`, and only `POST /api/v1/auth/login` and `/auth/refresh` lack the Bearer requirement.

## Verified behaviour

- **Field validation:** 400 `VALIDATION_FAILED`, with `errors` keyed by JSON path (`lines[0].quantity`) or `body`.
- **Unknown routes:** 404 `NOT_FOUND`, even for anonymous requests.
- **Auth:** 401 `UNAUTHENTICATED` without a token or with an invalid token; 403 `FORBIDDEN` for a role without the permission.
- **Correlation:** `X-Correlation-ID` is echoed on success, error and health responses; a missing or invalid value is replaced by a generated 32-character hex ID.
- **Rate limiting:** 429 `RATE_LIMITED` with `Retry-After`, per user or per address; `/health/live`, `/health/ready` and `/openapi/v1.json` are exempt.

## Remaining issues

- The repository `global.json` (`10.0.100` + `latestPatch`) does not match the installed SDK 10.0.401, and the committed version was `10.0.401`. Restoring `10.0.401`, or using `latestFeature`, needs an owner decision.
- The uncommitted M2/M3 files use LF line endings. `.editorconfig` requires CRLF, so `dotnet format --verify-no-changes` fails locally with `ENDOFLINE` only. `.gitattributes` (`eol=crlf`) normalises them on commit and checkout, and format passes after normalisation.
- ASP.NET Core's built-in hosting scope adds `RequestPath` (path only, never the query string) to every log entry when a provider records scopes.
- In-memory provider only: there is no database-backed readiness or concurrency evidence until M4.
