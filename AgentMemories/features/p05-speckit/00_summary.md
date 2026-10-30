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
| 008 gate-ban | `008-gate-ban` | DONE: 15/15 tasks. `f2b8d98` feat, review fixes commit "fix: ... (008 review)" (gate cache snapshot, ban scope validation, display-name Unicode rules, audit permission) |
| 009 identity-providers | `009-identity-providers` | DONE: 18/18 tasks, review (0 Blocker, 4 Major, 9 Minor) fixed before commit. ACC-03/29 with real devices still manual |
| 010 jobs-account-deletion | `010-jobs-account-deletion` | DONE: 55/55 tasks. `5c88392` feat (review 1 Blocker, 1 Major, 12 Minor; 2 Minor open in PCR platform §16) |
| (batch 2026-10-30) local-env, analytics, HTML docs | — | Owner tasks `tasks/task_30102026.txt`, plan + owner decisions `AgentMemories/features/task-30102026/00_plan.md`; runs BEFORE 011. Packaging (task 4) skipped, separate later |
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

## Technical notes from 008

- Gate document `gate/gate` is written through `IAuditedStore` (audit key `ops.gate` + request id, action in payload;
  replay returns `payload.after`). `StoreGateEvaluator` = volatile snapshot + generation counter (`IGateCacheInvalidator`).
- `StaffAdminService.SystemAudit(change, operation, permission, body?, action?)` builds platform audits; payload carries
  `permission`.
- Ban scope must be `login` or a registered command group; `scope: []` → 400.
- `DisplayNameRules.Normalize`: NFC stored, NFKC to `IDisplayNamePolicy`, invisible chars refused, ≤ 4 UTF-16 units per
  grapheme.
- Tests: 428/428 pass, including Couchbase.

## Technical notes from 009

- `IIdentityProviderAdapter` + `IdentityProviders` (deadline min(TimeoutSeconds, 0.8 × PlayerTimeout), bulkhead, link
  limit per player); built-ins `PlayGamesAdapter`, `GameCenterAdapter` (pins in `GameCenterTrust.cs`, host allowlist
  `static.gc.apple.com/public-key/`, no redirects); `FakeIdentityAdapter` default only in Development.
- Link = `identity.link` command using `TryClaimAsync(collection, key, attributes)`; first provider login inserts the
  mapping with platform `ClaimStore` (transaction; modules may not open transactions — arch test).
- Platform: `CommandOutcome.Reject(code, details)` with HTTP ≠ 200 → error envelope; `ICommandRunner.TryReplayAsync`
  runs before async decode (single-use codes).
- Failure classification: only client-caused proof failures count against the IP limit; server/pin problems →
  PROVIDER_UNAVAILABLE + LogError.
- Game Center chain verified 2026-10-27: gc-prod-12 (to 2027-05-27) → DigiCert Trusted G4 Code Signing RSA4096 SHA384
  2021 CA1 → DigiCert Trusted Root G4. Fixture `tests/GameServer.Platform.Web.Tests/Fixtures/gc-prod-12.cer`.
- Tests: 489/489 including Couchbase. `TelemetryTests.A_command_is_one_trace...` failed once under full-suite load
  (passes alone) — watch for flakiness.

## Technical notes from 010

- Jobs: `ModuleDescriptor.Jobs`, `LeaseJobRunner` (lease `leases::{job}`, CAS, fencing token, `nextRunAtUtc`, release on
  shutdown); off in `TestHost`, drive with `RunOnceAsync`. Per-job config key naming an unknown job fails startup.
  Never delete `leases::*` (token restart → purge records fence out; runbook in PCR platform §16).
- Every `CollectionSpec` declares a deletion treatment (NoPlayerData / Anonymize / Remove / ModuleStep) or startup
  fails. Platform provisions `ix_{c}_player_ref (playerField, META().id)` + partial due indexes (with `META().id`).
- Deletion: `identity.request_deletion` (`POST /api/v1/identity/delete-account`), GM `identity.cancel_deletion`
  (`player.ban`). Purge job `identity.account-purge` → `IAccountPurgeService` → `IAccountPurgeWriter` (only writer, arch
  test; ≤25 docs per fenced transaction). Order: receipts → anonymize → module steps → verify → other aggregates →
  remove:* → final (profile last). Receipts lose `resultCompact`, `bodyHash`, `requestId`, `correlationId`; gm_audit
  `payload.correlationId` blanked — otherwise `ReceiptIds.Compute` re-links anon id (B1).
- `ProfileCache` never caches a missing profile (M1). `afterExecute` hook failures are logged, response stays committed.
- `IGmTargetValidator.ValidateAsync(playerId, operation, ct)` — breaking change for game validators.
- Tests: 623/623 including Couchbase (`CouchbaseLeaseAndPurgeTests` ~4 min).

## Current state (2026-10-28, session restart)

- Branch chain (local only, **not merged into main, not pushed**): `main` (fda9f00) → `007-gm-security` (89bb3b3) →
  `008-gate-ban` (7fd9f19) → `009-identity-providers` (HEAD). Working tree clean after this resume commit.
- `.specify/feature.json` (gitignored) still points to `specs/009-identity-providers`; slice 010 must repoint it.
- Review summary of 009 is recorded at the end of `specs/009-identity-providers/tasks.md`.
- No host is running.

## How to resume

1. Read this file, then `specs/009-identity-providers/{spec,plan,tasks}.md` as the latest template for slice docs.
2. `git checkout -b 010-jobs-account-deletion` from `009-identity-providers`.
3. Per slice: spec + plan + tasks (commit `docs: add spec, plan and tasks for 0NN …`) → implement → full tests incl.
   Couchbase → tech-lead-reviewer → fix → `feat:` commit → update this file (`docs: update P0.5 resume after 0NN`).
   No stop for owner review (the single gate was after 007 spec+plan). Owner runs slices autonomously.
4. Commits: conventional, identity hung-bui-121, **no Claude signature / Co-Authored-By lines** (owner rule).

## Environment

- dotnet: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
  `test --solution GameServer.slnx -c Release --no-build` (single class: `--project X --filter-class "*Name"`).
- Couchbase EE 7.6.12 local (service `CouchbaseServer`, buckets `gameserver-it`, `gameserver-dev`). Tests need
  `COUCHBASE_CONNSTR=couchbase://localhost`, `COUCHBASE_USERNAME=Administrator`, `COUCHBASE_PASSWORD` = user env var
  `COUCHBASE_PW` (read via `[Environment]::GetEnvironmentVariable('COUCHBASE_PW','User')`, never print),
  `COUCHBASE_BUCKET=gameserver-it`. Run through PowerShell.
- Do not grep recursively over `samples/` (Unity `Library/` hangs it). Unity sample client = KestrelDemo.
- Secrets never committed/logged: pepper, Couchbase password, `DataProtection:MasterKey`, `Gm:Oidc:ClientSecret`,
  `Identity:Providers:PlayGames:ClientSecret`.

## Open items

- ACC-03/ACC-29 with real Play Games / Game Center devices: manual before release.
- Couchbase outage drill `tools/local/couchbase-outage.ps1` needs admin; owner has not run it yet.
- TBD-21 (GM frontend) open; another developer owns it.
- Possible flaky `TelemetryTests.A_command_is_one_trace...` under full-suite load.
- Game Center pins: gc-prod-12 expires 2027-05-27; Apple chain change → `PROVIDER_UNAVAILABLE` + LogError, update pins.
- Decide when to merge 007–009 (and later slices) into main and push; owner has not asked yet.

**Next:** slice 010 jobs-account-deletion (minimal lease jobs, PRIV-01 deletion with 30-day grace, purge and
anonymization; ACC-37), branched from `009-identity-providers`.
