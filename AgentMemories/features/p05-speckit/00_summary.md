# RESUME: P0.5 (Production hardening), then P1, using Spec Kit

**Started:** 2026-10-27.

**Owner decisions (2026-10-27):**
- P0.5 comes before P1.
- P1 is full scope, including MSG-01 and ANA-01.
- The workflow stops for review only once, after the spec and plan of slice 007.
- The GM frontend is out of scope and goes to another developer. Slices deliver the GM API plus the OpenAPI document
  `docs/api/gm-openapi.json`. TBD-21 stays open.

P0 is merged into `main` and pushed (fda9f00). The real-environment test tools from 2026-10-27 are in
`docs/guides/local-real-env-testing.md`.

## P0.5 slices (sequential branches from main)

| Slice | Branch | Status |
| --- | --- | --- |
| 007 gm-security | `007-gm-security` | DONE: 35/35 tasks. `5232fa1` feat, `3fce8ab` review fixes (0 Blocker, 5 Major, 8 Minor, all fixed) |
| 008 gate-ban | — | Maintenance + minClientVersion (gate document), ban/unban (GM-07), PERF-08 TTL, PRF-01 (ACC-30, 31) |
| 009 identity-providers | — | PGS v2 + Game Center, link/conflict/SWITCH_ACCOUNT, SDK (ACC-03, 29) |
| 010 jobs-account-deletion | — | Minimal lease jobs, PRIV-01 deletion with 30-day grace, purge and anonymization (ACC-37) |
| 011 giftcode | — | GFT-01..06, GM-04 API, reaper (ACC-08, 22, 23) |
| 012 iap-notifications | — | IAP-03 S2S: Google RTDN, Apple v2, refund/revoke |
| 013 ops-readiness | — | Secret scan in CI, PERF-05 budgets, load test report (TST-03), Linux/container guide, runbooks, deploy/rollback, restore drill (ACC-16, 19, 48; ACC-19 blocked on TBD-09) |

After P0.5 comes P1: Event, durable jobs (JOB-01/02, ACC-15), GM-05 API, PRIV-02, GFT-07, MSG-01, ANA-01.

## Technical notes from 007

**Audited writes**
- `IAuditedStore` (Platform) is the transaction for non-player documents (staff, key ring). Any transaction that writes
  must insert exactly one `gm_audit` entry.
- `gm_audit` is a platform collection.
- A collection declared with `CollectionSpec(TransactionOnly: true)` refuses plain key-value writes. The platform adds
  `dataprotection` to that set itself.

**Data Protection**
- One key ring document lives at `dataprotection/keyring`.
- The repository serves a snapshot from memory, so there is no sync-over-async. `KeyRingService` loads it in
  `StartingAsync` and refreshes it every 5 minutes.
- Requests never create keys (`DisableAutomaticKeyGeneration`).
- When a key activates, `Invalidate()` runs so Data Protection rebuilds its ring.
- Data Protection uses the system clock, not `TimeProvider`.

**CSRF**
- The check runs in `ProfileEnforcementMiddleware` after authentication.
- A token is bound to the session through `NameIdentifier = gmsession:{key}`.
- A Public endpoint that calls `RequireCsrf()` authenticates the cookie optionally first.
- `Origin` in the `Gm:Cors` allowlist skips the cross-site check but still needs the token.

**SSO**
- The OIDC callback is an explicit Public endpoint that calls `IAuthenticationRequestHandler.HandleRequestAsync` (the
  pipeline has no `UseAuthentication`).
- Uses `response_mode=query`.
- Tests use the fake IdP in `tests/GameServer.Gm.Tests/Infrastructure/FakeOidcProvider.cs`.

**Endpoint-mapping pitfalls**
- A lambda that takes only `HttpContext` binds to `RequestDelegate` and its result is dropped. Cast it to
  `Func<HttpContext, Task<IResult>>`.
- The routing 405 endpoint is answered with `METHOD_NOT_ALLOWED`.

**Testing pitfalls**
- Couchbase reports a lost concurrent transactional insert as `DocumentExistsException`. Retry by reading again.
- In tests, cookie expiry follows `FakeClock`. `CookieJarHandler` in `tests/Shared/GmBrowser.cs` drops `expires`.

**Tests:** 392/392 pass, including Couchbase.

**Next:** slice 008 gate-ban, branched from `007-gm-security`.
