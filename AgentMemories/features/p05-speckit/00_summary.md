# RESUME: P0.5 (Production hardening), then P1, using Spec Kit

**Started:** 2026-10-27.

**Owner decisions (2026-10-27):**
- P0.5 comes before P1.
- P1 is full scope, including MSG-01 and ANA-01.
- The workflow stops for review only once, after the spec and plan of slice 007.
- ~~GM frontend out of scope~~ — superseded 2026-11-05: owner builds the GM console as framework slices GM-1..GM-4, design
  `AgentMemories/features/gm-tool/01_design.md`. Slices still deliver the GM API plus the OpenAPI document
  `docs/api/gm-openapi.json`, now also served in every environment by `gm.console_catalog` (slice 017).

P0 is merged into `main` and pushed (fda9f00). The real-environment test tools from 2026-10-27 are in
`docs/guides/local-real-env-testing.md`.

## P0.5 slices (sequential branches from main)

| Slice | Branch | Status |
| --- | --- | --- |
| 007 gm-security | `007-gm-security` | DONE: 35/35 tasks. `5232fa1` feat, `3fce8ab` review fixes (0 Blocker, 5 Major, 8 Minor, all fixed) |
| 008 gate-ban | `008-gate-ban` | DONE: 15/15 tasks. `f2b8d98` feat, review fixes commit "fix: ... (008 review)" (gate cache snapshot, ban scope validation, display-name Unicode rules, audit permission) |
| 009 identity-providers | `009-identity-providers` | DONE: 18/18 tasks, review (0 Blocker, 4 Major, 9 Minor) fixed before commit. ACC-03/29 with real devices still manual |
| 010 jobs-account-deletion | `010-jobs-account-deletion` | DONE: 55/55 tasks. `5c88392` feat (review 1 Blocker, 1 Major, 12 Minor; 2 Minor open in PCR platform §16) |
| 011 local-env (batch 2026-10-30 tasks 1+2) | `011-local-env` | DONE: 57/57 tasks + T048a. Flavors, file store, `--provision`, run-local scripts. Review CONDITIONAL (0 Blocker, 4 Major, 9 Minor, all fixed) |
| 012 analytics-audit (batch task 3, ANA-01 part 1) | `012-analytics-audit` | DONE: 68/68 tasks. Login/Payment JSONL audit log. Review CONDITIONAL (0 Blocker, 4 Major, 8 Minor; all fixed except Minor 8 = owner policy) |
| (batch 2026-10-30) HTML docs | `docs-html-guides` | DONE: task 5 → `docs/html/{index,01-gioi-thieu,02-them-command,03-deploy}.html` (self-contained, drafts in `AgentMemories/features/task-30102026/docs-draft-5{a,b,c}-*.md`; HTML is now hand-maintained, drafts are historical). Accuracy review: reviewer followed 02 to add `counter.reset`, build + tests green. Also startup-validation fixes D1–D4 + m5/m6 (see notes). 5c checklist finished in ops-readiness. Packaging (task 4) skipped, separate later |
| 013 consumer-readiness (improvement round, FG group A) | `013-consumer-readiness` | DONE: 81/81 tasks + B1. FG-010 (Dev lockdown), FG-001/006 (TestKit), FG-025 (request log), FG-002/003/004 (run-local, build seal), FG-007/011/012 (version, error band, consumer guide). Review CONDITIONAL (0 Blocker, 0 Major, 13 Minor, all fixed `b8a8623`) |
| 014 wire-contract (FG group B) | `014-wire-contract` | DONE: 87/87 tasks. FG-026 direction B (route catalog + pre-serving endpoint check), FG-028 (export mode + WireGen), FG-021 (typed wire mappers), FG-027 (TestKit WireConformance). Review r1 NO (B1 + 2 Major + 9 minor, fixed `7024791`), r2 CONDITIONAL (5 minor, fixed `b1ff174`) |
| 015 config (FG group C) | `015-config` | DONE 71/72 (T058 Android/iOS device run = OWNER, tag waits on it). FG-018 (declared config rules + eager catalogs), FG-023 (isolated startup checks, GameConfigStatus), FG-024 (+ additive `client_config_version`), FG-019 (compression, PersistentConfigCache). Review r1 CONDITIONAL (3 Major + 11 minor + walkthrough G1–G4, decisions `specs/015-config/review-decisions.md`, fixed `8ac0023`), r2 CONDITIONAL (7 minor, fixed `1cb4a2e`) |
| 016 login-command (FG group D) | `016-login-command` | DONE 83/84 (T069 device session = OWNER, also covers 015 T058; tag waits on it). FG-013 (`is_new_player`), FG-015 (account initializers), FG-029 (degraded reads), FG-016 (COMMAND_OUTPUT_TOO_LARGE + details), FG-022 (SDK request id + pending store). Review r1 CONDITIONAL (B1 + 3 Major + 10 minor, fixed `d5153da`), r2 CONDITIONAL (1 Major regression + 6 minor, fixed `a7b3d6b`). Round release note draft `docs/releases/v0.7.0.md` |
| 017 gm-api-contract (GM tool GM-1) | `017-gm-api-contract` | DONE 2026-11-05: 64/64 tasks, `e182fba` feat (not merged to main, not pushed). Full suite incl. Couchbase green (2875, 0 failed; one later run hit a local Couchbase query timeout in the fixture, project rerun green). Review tech-lead r1 CONDITIONAL (1 Major + 6 minor) and r2 CONDITIONAL (1 Major + 3 minor), all fixed in `e182fba`. GM document in every env + `gm.console_catalog` (ETag/304), `gm.auth_methods`, `/me` versions, DataAnnotations enforced with `errors`, option routes, paged lists, kit `GmOpenApiSnapshot`, fixture GM dogfood. Review r1 CONDITIONAL (0 Blocker, 4 Major, 6 minor; M1–M3, m1–m3, m5, m6 fixed in T063; M4, m2, m4 = owner decisions). Release note draft `specs/017-gm-api-contract/release-notes-draft.md` |
| 018 gm-console-hosting-shell (GM tool GM-2) | `018-gm-console-hosting-shell` | DONE 2026-11-07: `80776c0` feat (phase B), merged ff into main + pushed 2026-11-07; 70/70 tasks. Phase A `63ddb8f` (cookies `__Host-gs_gm`/`__Host-gs_gm_csrf` outside Development, `gs_gm_dev`/`gs_gm_dev_csrf` in Development, `Path=/`, `Gm:Cookies:SameSite` (Lax), CORS expose + max-age 7200 + one origin, SSO to `Gm:Console:BaseUrl`, `/gm/v1/ui` Dev/Test only; review T024 r1 CONDITIONAL 1 Major + 4 minor fixed, r2 YES). Phase B: `gm-console/` npm workspace (shell `@gameserver/gm-shell`, default app, `deploy/`, e2e smoke on system Chrome/Edge), docs (PCR gm/platform §24, `docs/guides/gm-console.md`, consumer guide §15). Review T068 r1 CONDITIONAL (2 Major + 8 minor) → T069, r2 YES (5 minor + spec FR-052/FR-054 amendment, done). Suites: in memory 3026 (0 failed), File 1 known-flaky TelemetryTests (rerun green), Couchbase 32 fixture provisioning timeouts (project rerun 151, 0 failed); `npm run verify` 231 tests; e2e Chrome 157 + Edge 156 green. Release note draft `specs/018-gm-console-hosting-shell/release-notes-draft.md` |
| giftcode | — | GFT-01..06, GM-04 API, reaper (ACC-08, 22, 23) — number after the improvement round |
| iap-notifications | — | IAP-03 S2S: Google RTDN, Apple v2, refund/revoke; add refund analytics event on the 012 seam |
| ops-readiness | — | Secret scan in CI, PERF-05 budgets, load test report (TST-03), Linux/container guide, runbooks, deploy/rollback, restore drill (ACC-16, 19, 48; ACC-19 blocked on TBD-09), finish deploy guide (task 5c), TLS release gate, analytics re-drive tool |

Slice numbers are "next free number" (owner Q18). HTML docs (task 5) is docs-only and may not take a slice number.

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

## Technical notes from 011

- Flavors: `GameServer:EnvironmentId` ∈ local/dev/beta/uat/live (`DeploymentEnvironments`) → ASP.NET env local/dev =
  Development, beta/uat = Staging, live = Production. Unknown or mismatched flavor stops startup; never silently
  Development. `TestHost` uses EnvironmentId `test` and skips `appsettings.{env}.json`.
- Startup: `IStartupCheck` + `GameServerApplicationExtensions.CheckAll` (each check caught, one aggregated report);
  `ConfigurationBinding.GetSettings<T>` turns binder errors into exit 1. `GameServerCommandLine.Parse` throws on bad
  switches (`--provision=x`, `-provision`, `/provision` → exit 1); `--provision` is case-insensitive.
- File store (`src/GameServer.Platform.Testing/FileStore/*`): `InMemoryDocumentStore` + persistence seam, one file per
  document, journal = commit point, `.lock`, `store.json` format version; refused outside Development before touching
  disk; open errors → `GameServerStartupException`. Sample data in `samples/SampleHost/_database/` (gitignored).
  Tests: `GAMESERVER_TEST_STORE=File`.
- Couchbase: `CouchbaseClusterOptionsFactory` (Snappy on; rollback `Couchbase:Compression=false`), Production requires
  `couchbases://`; `CouchbaseTrustedRoots` with `DisableCertificateDownloads = true`, file must be the root CA.
  `Couchbase:Password` required in every environment.
- `--provision` (exit 0/1/2): `CouchbaseManagementRetry` retries cluster-manager 503 (8 attempts, ~22 s); unknown
  collection surfaces as `UnambiguousTimeoutException` (reason `CollectionNotFound`); RBAC scope roles written
  `[bucket:scope]`; minimal provisioner roles (tested): `bucket_admin`, `query_manage_index`, `data_reader` on the bucket
  + `query_system_catalog` (without it the index is hidden from `system:indexes`). Index in state `error` fails at once;
  wait bounded by `ProvisioningTimeoutSeconds`. Provision mode refuses JSON-file secrets outside Development/Test.
- IAP: `IapOptionsValidator` checks 5 keys at startup when real verifiers are used (private key must sign, Apple root
  must load); fake verifier refused outside Development/Test.
- Local run: `tools/local/run-local.ps1|.sh` (port 5080, `-Port/-Reset/-Lan`, exit 2–7), launchSettings profiles
  `local`, `dev-couchbase`. Unity sample client now on 5080.
- Breaking changes listed in PCR platform §17.
- Tests: 930 (926 pass + 4 skipped: kill, perf, 2 TLS) incl. Couchbase ~19 min; in-memory/File ~35 s each.

## Technical notes from 012

- Seam: Core `AnalyticsEvents` + `ctx.AddAnalyticsEvent` (outbox kind `analytics`, reserved); Platform
  `src/GameServer.Platform/Analytics/*` (options `GameServer:Analytics` Enabled/Directory/TimeZone/DeliveryTimeoutMs,
  `AnalyticsPipeline` bounded queue 10k + batches 512, `AnalyticsOutboxConsumer`, formatter, time zone, metrics);
  Observability `Analytics/*` file sink + `.lock` per game+instance + readiness `analytics` (Degraded only);
  `AddAnalyticsFiles()` explicit + idempotent; `TestHost` uses `InMemoryAnalyticsSink` (zone UTC).
- Payment: outbox entry in the grant transaction; userID/chargeTime/receiptId/eventId taken from the receipt at delivery
  (010 purge covers it). Ack only after every sink wrote + **fsync** (SC-004 amended: purchase p95 +≤100 ms, measured
  +58 ms dev; revert = `flushToDisk: false`). 500 ms inline bound, else sweeper/backoff/dead-letter. eventId =
  SHA-256(`{entryId}|{committedAtUtc}`). transactionID `apple:{id}` / `google:{orderId}` / `google:h:{sha256[0..32]}`.
  `VerifiedPurchase` + `IsTest`, `PurchasedAt`, `PurchaseCategory`; fake verifier = test purchases.
- Login: best effort after session (`LoginCompletion`); resume event in `PlayerBearerAuthenticationHandler`, ≤1 per
  player/local day/instance, `DailyPlayerSet` lock-free fast path, cap 200k.
- Startup fails (when analytics active): bad/missing zone in Staging/Production, Windows zone id, unwritable dir,
  duplicate instance id, **Enabled with no sink outside dev**. `--provision` needs no analytics settings.
  `UseGameServer` reports options-validation failures from relay construction (no longer swallowed).
- Economy claim key: `extref::{type}::{ref}` up to **246 bytes** (measured Couchbase 7.6 named-collection limit, not
  250), longer → `extrefh::{type}::{sha256 hex}`.
- Banned: `TimeZoneInfo.Local`, `DateTime.Today`. JSON formatter uses `UnsafeRelaxedJsonEscaping` (safe for JSONL).
- Docs: PCR platform §18, PCR iap/identity, `docs/guides/analytics.md`, environments guide §7.
- Tests: 1174 (in-memory 1089 pass + 85 skip); Couchbase ~23–25 min.

## Technical notes from docs-html-guides (2026-10-31)

- Command routes carry `CommandOperationMetadata`; `CommandRouteValidator` (Platform.Web/Validation) fails startup for:
  operation no module registers (ARC-07), route profile ≠ command profile (SEC-08), GM route permission ≠ command
  permission (SEC-07). Routes mapped after `UseGameServer` are not checked. Command without route is allowed.
- `CommandRunner.EnsureCommittableCode`: `Commit(result, code)` needs the catalogued instance (core or any AddModule'd
  module) with HTTP 200, else 500 INTERNAL and nothing written (**breaking**, PCR platform §8/§12). Old receipts with
  uncatalogued code replay OK + Warning. `Reject` unchecked.
- `ErrorCodeSet` rejects null entries (ErrorCode fields declared below `All`).
- `docs/guides/module-authoring.md` fixed (Deletion treatment, used error ranges incl. identity 14000).
- HTML docs: footer "Đúng với commit 175df65 + sửa sau review"; DoD for later slices: update `docs/html` when the
  pipeline, hosting or deploy changes.

## Improvement round 2026-11-03 (zh-pvp-server friction log)

- Owner (2026-11-03) chose all four FG groups: 013 = A, 014 = B, 015 = C, 016 = D (table above). Deferred: FG-005/008/009
  (packaging, task 4), FG-014/017/030/031; FG-020 proposed `game-only`. **One annotated tag `v0.7.0` at the end of the
  round** (not per slice). Release notes: per-slice `specs/0NN-*/release-notes-draft.md`, merged into
  `docs/releases/v0.7.0.md` at round end.
- **After the tag, this session bumps the game's submodule** (`E:\ProjGits\zh-pvp-server`, `docs/framework-feedback/UPGRADE.md`
  §2) and messages the waiting game agent (local session `gameclient-65`, works across client + server; check
  ListAgents). Ask the owner before pushing branch/tag.
- Orchestrator defaults awaiting owner override: request log on by default local/dev/Test, off beta/uat/live (FR-086);
  no switch to allow cheats on Staging; version only via GM `ops.read`; `DeploymentEnvironment=Test` refused unless
  EnvironmentId `test` (BC-8, review m9). 014: `ReplaceWireMapper` must keep the framework message (M1 option
  "restrict", alternative = replaced section becomes origin `game`).

## Technical notes from 018

- **Cookies**: outside Development `__Host-gs_gm` / `__Host-gs_gm_csrf` (`Path=/`, `Secure`, no `Domain`, antiforgery `Always`); in
  Development `gs_gm_dev` / `gs_gm_dev_csrf`: run-local is plain HTTP and ASP.NET antiforgery with `SecurePolicy=Always` throws on
  HTTP while a `__Host-` cookie must be `Secure` (research R1); new names, because the pre-018 `gs_gm` (`Path=/gm`) would be
  sent first and shadow a `Path=/` cookie of the same name. `SameSite` comes from `Gm:Cookies:SameSite` (Lax; `None` only in
  Development/Test with a CORS origin). One-time sign-out for every staff member on upgrade.
- **H14** (CORS headers lost on error answers) **not reproduced** on ASP.NET Core 10.0.14: `CorsMiddleware` applies the headers in
  `OnStarting`, which `Response.Clear()` keeps; `GmCorsErrorHeadersTests` (12 statuses × 2 origins) is the permanent guard;
  `ErrorResponseWriter` unchanged.
- **e2e harness** `gm-console/e2e/` (`npm run e2e`, run-local started first, never `-Reset`): Vitest node + `playwright-core`
  with the **system** browser, never a download — Chrome `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe` (default),
  Edge `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe` via `GM_E2E_BROWSER`; targets `dev` and `preview` (built app +
  `deploy/security-headers.json`) on 5173 strict; request headers, preflights and raw 304s read through CDP; reports in
  `e2e/.artifacts/` (ignored). `GM_E2E_API`: the preview target serves a generated `e2e/.artifacts/config.json` for it; any API other
  than `http://localhost:5080` skips the dev target (it serves `app/public/config.json`); run-local prints the console banner line only
  on port 5080. Port 5180 is never used.
- **Toolchain pins** (rule: newest stable ≥ 14 days old that runs on Node 22.20, exact versions, `engine-strict`, lockfile v3):
  React 19.3.1, Mantine 9.7.3, TanStack Query 5.104.5, React Router 7.18.4 (8 needs Node ≥ 22.22), openapi-fetch 0.17.0,
  openapi-typescript 7.13.0, TypeScript 5.9.3 (6/7 outside typescript-eslint and openapi-typescript peers), Vite 8.3.5, Vitest 5.0.5,
  jsdom 29.1.1, ESLint 10.13.0 + typescript-eslint 8.72.0, playwright-core 1.64.1. npm 10.9.3 needs root
  `"overrides": { "vitest": "5.0.5" }` (Arborist `edgesOut` crash through vite's optional `@vitejs/devtools` peers). `gm-console/.npmrc`
  blocks the scope: `@gameserver:registry=https://registry.invalid/` (dependency confusion; workspaces still link locally; a game root
  needs the same line). `GmConsoleWorkspaceTests` pins exact versions, the shell link in the lockfile and the framework version.
- **Mutation boundary (D10)** in three layers: lint (`gm-console/eslint.rules.js`: any use/destructuring of `POST/PUT/DELETE/PATCH/
  request`, non-literal computed calls, raw `fetch` outside client/config/session; `session/` only `.POST` with the literal
  login/logout path; fixtures in `eslint.rules.test.ts`), the source scan `mutationBoundary.test.ts`, and the client's run-time guard
  `MUTATION_NOT_ALLOWED` (GM-3 replaces it with the catalog check). Session token after a sign-in is fetched lazily (spec FR-052/054
  amended).
- **CSP**: static host keeps `style-src 'self'` (V3): Mantine runs with `withCssVariables={false}`, `withGlobalClasses={false}`, no
  responsive style props (`SimpleGrid` injected a `<style>`), in-memory colour-scheme manager (default one uses `localStorage`).
- **Build**: `base: './'`, `copyPublicDir: false` (no `config.json` in `dist/`), `check:dist` 500 KB gzip budget (185.4 KB).

## Technical notes from 017

- Catalog: `GmCatalogDocument` (Platform.Web, public) generates the GM document lazily through the keyed
  `IOpenApiDocumentProvider("gm")` (no request needed, V11), canonical bytes (STJ indented, LF, final newline, no BOM), strong
  ETag; served by `gm.console_catalog` (overrides the GM `no-store` with `private, no-cache`) and by the kit.
- One walk (`GmBodyContract`, STJ contract, property + positional-parameter attributes, depth 3) feeds the schema
  transformer and `GmBodyValidator`. V1: the generator only reads `[property:]` attributes, ignores `[Required]`, lists every
  positional parameter as required and puts nested-property keywords on components → our transformer overwrites. The
  generator wraps nullable objects as `oneOf: [null, $ref]` only after schema transformers → `GmDocumentTransformer` drops the
  null branch of required objects (request bodies marked via `OpenApiSchema.Metadata`).
- `Accepts<T>` is not metadata only: routing's "415 HTTP Unsupported Media Type" endpoint has no profile →
  `ProfileEnforcementMiddleware` maps it to 415 (was 403).
- Route names without a dot are refused by the catalog check on every host (so "whole-name tag" is only a unit case).
- Enums: the schema transformer writes the camelCase strings of `GameServerJson.Options` (review r1 m1 removed the host-wide HTTP JSON enum converter; was BC-9); only runtime user of those options in the framework = counter GM read).
- Open owner questions: release note "Decisions awaiting owner override" 1–9 (D9 versions on `/me`, D5–D7, D10, D14, D15,
  A3/A4, review M4 depth limit, m4 option-route permission, m2 relative kit paths).

## Technical notes from 016

- Owner confirmations open (listed in `docs/releases/v0.7.0.md` with 013/014/015 ones): creation receipt only when an
  initializer changed something; `CommandResponse.details` carries module rejections too; sample seeding via optional
  `account.json`; release note split (draft in 016, round-close separate); login store failures answer
  COMMIT_UNKNOWN_RETRY/CONCURRENT_UPDATE (BC-10); same-player re-login keeps last good copy of unavailable sections.
- Login: `IAccountWriter` (Platform.Commands) decides create/update per transaction attempt; `is_new_player` and
  analytics createdPlayer from the committed attempt; ≤3 race rounds then CONCURRENT_UPDATE. Creation receipt
  `identity.create_account`, System actor + player id, random request id, only when initializers had effects.
- Initializers: `AccountInitializers` in ModuleDescriptor, name `<first module segment>.<name>`, profile + union ≤ 4
  aggregates, ≤ 2 claims, per-initializer view, NewId/NewSeed prefixed by name; claims checked (empty key, provisioned
  collection) before the read; store exceptions marked `StoreFailureKey` pass through. Failure → INTERNAL, one log line
  (`ReportedFailure` marker suppresses ExceptionMiddleware line).
- Degraded reads: `AggregateReadFailures` (AGGREGATE_VERSION_AHEAD 503 / AGGREGATE_UNREADABLE 500), `state` must be a
  JSON object, `SectionWriter` single mapping place, unavailable sections listed never empty, GM `unreadable` list,
  bearer handler returns Fail(code) (V5: single-scheme hosts bypass error middleware — pre-existing follow-up).
- Commands: every commit rule incl. receipt size before staging; COMMAND_OUTPUT_TOO_LARGE (HTTP 200) with
  `CommandResponse.details` (11) part/bytes/limit; `GameServer:Commands:*` ranges validated (ReceiptMaxBytes ≥ 1112).
  `CommandRunner` ctor takes ILoggerFactory (BC-12). Shared `ReceiptBuilder` = kit parity (exact only with matched
  actor/request/receipt ids).
- SDK: `SendCommandAsync(route, req, parser, string? requestId, ct)` (4th param required, null = generated),
  `NewRequestId()`, `GameServerClientOptions.PendingCommands` (Persistent/InMemory/UnityPendingCommandStore, per-entry
  `.pending` files via CacheFileOps, Flush(true), record p95 ~5.6 ms on C:); `PendingCommands` read-only snapshot; no
  auto resend; 23 h expiry on server time; auth answers keep entry, ACCOUNT_DELETED final. AvailabilityChanged fires
  after the copy is stored.
- Sample: `samples/SampleHost/config/base/account.json` seeds 100 gold (tests/Shared config unseeded). KestrelDemo E2E
  `-e2eDevice` switch. APK `samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk` (rebuild with LAN serverUrl for T069).
- Tests at `a7b3d6b`: 2751 total; in-memory/File 2640 pass / 111 skip; Couchbase project 148 pass / 3 skip. Full
  Couchbase at `d5153da`: 2714 pass / 25 skip; 11 opt-in perf pass. Known flaky: two TelemetryTests,
  IapAnalyticsTests.A_slow_sink…
- Follow-ups: m9 broken-profile log volume; V5 single-scheme auth; CommandRunner.cs ~560 lines.

## Technical notes from 015

- Architect decisions awaiting owner override (release notes list them): additive `client_config_version` (ErrorEnvelope 10,
  CommandResponse 9, SnapshotResponse 4, GetConfigResponse 5, LoginResponse 7) instead of redefining `config_version`
  (API-03); D3 strictness (duplicate + case-variant property names fail startup, integer `kind` refused, maxQuantity ≥ 1);
  BC-9 (provision never loads config; config-needing checks warn). zh-pvp config passes all (checked T067).
- Config phase = first block of `UseGameServer`: load once → required files → module rules (`ConfigDeclarations` in
  `ModuleDescriptor`, `ConfigJson` helpers, lines `[module m] config f row n: …`) → catalogs (platform-registered,
  last registration wins, BC-8; optional-absent file → build still runs; backstop line for unexplained unbuilt catalog)
  → bundle size (BC-7) → `GameConfigStatus` (never throws; `TryGetCatalog<T>`). Startup checks created one by one
  (BC-10). TestKit: `ConfigDeclarationCheck`, `TestGameConfigStatus`; `LocalFlavorFactory` throws
  `GameServerStartupException` on refused start.
- Duplicate JSON properties: own walk `JsonDuplicateProperties` (V1 fallback), OrdinalIgnoreCase.
- Versions: server hashes clientVisible set; client version = client-visible files + set + label. Control chars in
  manifest names rejected.
- Compression: per request in GetConfig, only with Accept-Encoding, gzip 6 / Brotli 4, startup probe per encoding,
  per-host gate max(1, CPUs/4), fallback = identity + `config_compression_skipped_total{reason}`. Unity Editor/Windows
  send `deflate, gzip` and decode gzip (V8), no SDK decoder.
- SDK: `PersistentConfigCache` (Runtime/Core), `UnityConfigCache` (Runtime/Unity, iOS no-backup flag); cache name rule
  `[a-z0-9._-]{1,64}`, no leading/trailing dot, no Windows device names. Save 1 MiB p95 ~11–15 ms on C:, ~70 ms on D:
  (Flush(true) kept).
- APK for T058 at `samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk` (gitignored); owner steps in tasks.md T058.
- Tests at `1cb4a2e`: 2321 total; in-memory 2219 pass / 101 skip (1 flaky `TelemetryTests.Exported_spans_and_metrics…`,
  passes alone — second flaky telemetry test); File 2220 / 101 / 0. Couchbase at `8ac0023`: 2289 pass / 23 skip.
  Re-run Couchbase before the tag.

## Technical notes from 014

- Routes: `RouteDeclaration` (Core) listed in `ModuleDescriptor.Routes` / `PlatformRoutes` / `GameServerBuilder.DeclareRoutes`;
  map via `MapDeclared`/`MapPlayerCommand(route)`…; `WireCatalog` keeps a reference-equality set — an endpoint mapped
  from an unlisted declaration fails startup (B1). Route conditions checked on mapped endpoints too.
- Endpoint check: `EndpointSetValidationService` (IHostedLifecycleService, registered first) in `StartingAsync`, before
  Kestrel binds (V1 held). R8/V16 option b: ASP.NET rebuilds endpoints on every `DataSources.Endpoints` read (~2 ms
  each), so `UseGameServer` enumerates endpoints only when it already has errors; on a good config the check runs once
  before serving and the "Starting …" line moves there (BC-2). Validation ≈1 ms for 217 endpoints.
- Export: `--export-wire-contract <path>` → internal arg `--GameServerCommandLine:ExportWireContract=`; never listens,
  needs no store/secrets, atomic write via `<name>.<guid>.wire-contract.tmp`.
- `tools/GameServer.WireGen` generate/verify (exit 0/1/2; 2 only for verify; `[internal]` → 1), protoc from Grpc.Tools
  2.84.0 (`macosx_universal`), `--dotnet` lookup order, configs: `sdk/GameServer.Client/wiregen.json`,
  `samples/SampleGame/wiregen.json`, `tests/ConsumerFixture/tools/wiregen.json` (`CommittedOutputTests` verifies all three).
  Health probes not generated. V8 namespace rule copies protoc (letter after digit capitalised).
- Mappers: `AddAggregateWireMapper<TState,TWire>` + `Hidden`/`MayStayDefault`; untyped `[Obsolete]`; duplicate fails
  startup (BC-4); TState must match aggregate StateType (or base); `ReplaceWireMapper` must keep the message.
- TestKit `WireConformance` (Check/CheckModules/CheckAll — only CheckAll sees overrides); samples 2 elements, distinct
  enum values, first+last index perturbed; uses aggregate StateType.
- SDK 0.7.0: `SectionKey<T>`/`SectionTable`/`SectionRead`, generated `Runtime/Generated/FrameworkRoutes.cs` +
  `FrameworkSections.cs`; KestrelDemo uses generated `SampleGameRoutes`/`SampleGameSections` (.meta by Unity, T072 E2E OK
  with Unity 6000.3.22f1 at `D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe`).
- T082: zh-pvp CRLF regen in a temp clone reproduces `ZhpvpGame.cs` byte-identical after BC-3 + BC-1; adds
  `GameRoutes.cs`/`GameSections.cs`.
- Tests at `7024791`: 1858 total; in-memory/File 1762 pass / 96 skip; Couchbase 1840 pass / 18 skip (~5 min). After
  `b1ff174`: 1866 total; in-memory/File 1770 / 96 skip; Couchbase 1848 pass / 18 skip (5 min 13 s).
- Open: Linux/macOS WireGen determinism (N9) manual; `SampleDocument.cs` ~250 lines.

## Technical notes from 013

- Dev lockdown: `GameServerOptions.AllowsDevProfile` (= AllowsDevelopmentTools) is the one rule for
  `EndpointSecurityValidator`, `ModuleRegistry.ValidateCommands` (incl. service-only, `--provision`),
  `ProfileEnforcementMiddleware` (Dev refused before auth; unknown/missing profile → 403) and
  `CommandRunner.EnsureActorAllowed` (execute + replay, before PlayerId check). Commit checks live in internal `CommitRules`.
- `src/GameServer.TestKit` (consumer test kit): ProtobufHttp, IdentityHttp, TestStores, GmBrowser, TestHost
  (Identity-only), LocalFlavorFactory<T> (8 settings), CapturedLog, TestGameConfig, CommandContextDouble (wraps internal
  CommandContext; HardBytes + result size via CommitRules; receipt size NOT checked; no-op sets not recorded).
  Framework tests use `tests/Shared/SampleTestHost`. `TestKitReferenceCheck` fails startup outside Development/Test.
  `tests/ConsumerFixture` = game-shaped repo in the slnx.
- Request log: outermost `RequestLogMiddleware`, public `IRequestOutcomeFeature` / `context.RequestOutcome()` (reused if a
  consumer created it first), category `GameServer.Requests`; unmatched paths log first segment only (`/api/...`);
  `GameServer:RequestLog:{Enabled,ExcludedPaths}`; console sink `Observability:Logging:Console:*` (bounded, dropping,
  control chars cleaned); `log_dropped_total{sink=file|console}`; `request_log_failures_total`.
- Build: empty root `Directory.Build.targets`; `IsTestProject` relative to framework root; `BuildSealingTests` (hostile
  parent incl. `Directory.Packages.props`), `tools/local/check-build-seal.ps1`. run-local `-HostProject/-LaunchProfile/-Name`
  via `tools/GameServer.LocalRun describe`; passes `--project`; warns on bash < 4.4.
- Version: `GameServerFrameworkVersion` 0.7.0 in `Directory.Build.props` = Unity package version (test enforces);
  informational `0.7.0+sha`; startup line `Starting {GameId} {GameVersion} on framework {FrameworkVersion}…`;
  `service.version` + `gameserver.framework.version`; `GameServerRuntimeInfo` (one instance id); `GET /gm/v1/ops/version`.
- `ErrorCodeBands` 50000–89999 reserved for games (arch test); docs/template examples use 90000–90099.
- Bug B1 fixed: `DailyPlayerSet.TryMark` re-checks before clearing a full set.
- Docs: `docs/guides/consumer-guide.md` (new), HTML pages stamped "framework v0.7.0 (chưa phát hành)" — restamp at tag.
- Tests after review fixes: 1482 total; in-memory and File 1387 pass / 95 skip; Couchbase 1464 pass / 17 skip (full suite ~5 min, Couchbase project alone 4 min 14 s — was ~24 min in 012, reason not investigated). Known flaky TelemetryTests failed once, passed 3/3 on rerun.
- Open: `run-local.sh` on macOS/Linux not run; `CommandContextDouble.cs` ~370 lines (split later); page 03 "→ 015"
  badges use the old ops-readiness number.

## Previous state (2026-11-02) — SESSION ENDED, framework work PAUSED

- **Owner ended the framework session on 2026-11-02.** Framework work restarts only when the owner brings
  improvements from the zh-pvp-server pilot. `main` = `cd7ff0b` (docs only after tag `v0.6.0` = `2ba963f`), pushed,
  working tree clean (only the owner's untracked `tasks/`). No slice in progress, no untagged code change.
- **Restart procedure for improvements** (see "How to resume" step 0).
- **PAUSED 2026-10-31 at owner request.** `main` fast-forwarded to `13c043a` (010 + 011 + 012 + docs-html-guides)
  and pushed. Last full suite incl. Couchbase: 1190 tests, 0 fail, 7 skip.
- Merged into main: `010-jobs-account-deletion` (5c88392), `011-local-env` (ab17cc0), `012-analytics-audit`
  (175df65), `docs-html-guides` (2ea064f, 8c586b3, 13c043a). Branches still exist on origin.
- `.specify/feature.json` (gitignored) points to `specs/012-analytics-audit`; next slice must repoint it.
- Review summaries are at the end of each `specs/0NN-*/tasks.md`.
- Owner batch 2026-10-30 done except task 4 (packaging, deferred by owner).
- Owner has NOT answered yet: start 013 giftcode? Apple sandbox receipts on live? Live Couchbase replica count and
  deploy-checklist sign-off roles? Orchestrator decisions awaiting owner override: payment fsync before ack
  (SC-004 +≤100 ms), analytics Enabled without sink outside dev fails startup, economy claim key fixed inside 012.
- Next work (only when owner says so): 013 giftcode, branched from main.
- **2026-11-02: first tag + first consumer.** Annotated tag `v0.6.0` on `2ba963f` pushed (release notes list the
  not-ready-for-Live items). First consumer: game repo `E:\ProjGits\zh-pvp-server` (company GitLab, no remote yet)
  uses this repo as submodule `framework/` pinned to the tag. Its friction log
  `docs/framework-feedback/FRICTION_LOG.md` (FG-001..FG-012) is the input for future framework slices. Most
  important: **FG-010 (HIGH, security)** Dev-profile (cheat) endpoints are refused only in Production
  (`EndpointSecurityValidator.cs:86`), accepted in Staging with a player bearer (`ProfileEnforcementMiddleware`
  default branch, `CommandRunner.cs:154` Dev => true). Others: test helpers not reusable outside (FG-001), run-local
  hardcoded to SampleHost (FG-002), no root `Directory.Build.targets` seal (FG-003), path-based `IsTestProject`
  (FG-004), File/InMemory stores in Platform.Testing (FG-005), no `ICommandContext` fake (FG-006), no version
  stamping (FG-007), consumer build settings (FG-008), host composition copied (FG-009), no error band for games
  (FG-011), `02-them-command.html` written for in-repo modules (FG-012). Framework changes go through a slice here,
  then a new tag; the game bumps the submodule.

## How to resume

0. **Improvement round from a consumer game** (the expected way this session restarts):
   - Read the game's friction log `E:\ProjGits\zh-pvp-server\docs\framework-feedback\FRICTION_LOG.md` (status
     `framework-candidate` / open entries, triage notes, metrics) and its `docs/framework-feedback/UPGRADE.md`.
     Do not edit the game repo from here; the game session owns it.
   - Confirm with the owner which FG entries to take. Suggested first slice "consumer-readiness": FG-010 (HIGH
     security, refuse Dev profile whenever `!AllowsDevelopmentTools`), FG-001/FG-006 (reusable sample-free test
     helpers + public fake `ICommandContext`), FG-003 (empty root `Directory.Build.targets`), FG-004 (relative
     `IsTestProject` check), FG-002 (run-local `-HostProject`/`-LaunchProfile`), FG-007 (version stamping),
     FG-011 (reserve a game error band in module-authoring), FG-012 (02-them-command for external consumers).
     FG-005/FG-008/FG-009 belong with task 4 (packaging/NuGet).
   - Run it as a normal slice (step 3), then: merge main, annotated tag `v0.6.x`/`v0.7.0` with release notes naming
     the FG ids fixed, push the tag, and update the UPM URL tag in docs if the Unity SDK changed.
   - Tell the owner the new tag; the zh-pvp-server session bumps its submodule (`chore: bump framework to <tag>`),
     removes the matching `FRAMEWORK-GAP` workarounds and marks the entries `fixed-in-<tag>`.
   - Other pending owner decisions (013 giftcode, Apple sandbox, replicas/sign-off roles) are listed above.
1. Read this file, then `specs/012-analytics-audit/{spec,plan,tasks}.md` as the latest template for slice docs.
2. Branch the next slice from `main` (010–012 + docs merged 2026-10-31).
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

- **Sample cleanup (owner 2026-11-04, deferred — do later, not scheduled):** too many sample-like places confuse
  devs (`modules/Sample.Counter`, `samples/SampleGame` + SampleHost + KestrelDemo, `templates/module`,
  `tests/ConsumerFixture`). Owner chose **SampleGame as the single sample for case studies**: fold counter into
  SampleGame and remove `modules/Sample.Counter` (≈41 Platform.Web test files use it as the probe module → move to
  SampleGame or a test-only module in `tests/`), docs/HTML examples only on SampleGame (02-them-command §5 uses
  `counter.reset`). Still undecided: keep `templates/module` and `tests/ConsumerFixture` (proposal: keep, label "not a
  sample") and process (Spec Kit slice 017 vs light refactor). Ask the owner both before starting.
- ACC-03/ACC-29 with real Play Games / Game Center devices: manual before release.
- Couchbase outage drill `tools/local/couchbase-outage.ps1` needs admin; owner has not run it yet.
- TBD-21 (GM console) decided 2026-11-05: owner builds it as GM-1..GM-4; GM-1 = slice 017 `gm-api-contract` (DONE),
  GM-2 = slice 018 `gm-console-hosting-shell` (DONE 2026-11-07). 2026-11-08/09 owner: ONE GM URL for every game x env,
  admin grants access, central control plane (option C) - design `AgentMemories/features/gm-tool/02_single_url_multi_env.md`
  (accepted). Next: tag v0.8.0 (owner to confirm) then CP-1 `gm-token-auth`; GM-3 moves after CP-3.
- Possible flaky `TelemetryTests.A_command_is_one_trace...` under full-suite load.
- Game Center pins: gc-prod-12 expires 2027-05-27; Apple chain change → `PROVIDER_UNAVAILABLE` + LogError, update pins.
- 010 open (PCR platform §16): the `deletion` view field schema (generator limitation; the `deletion/cancel` body/response
  schema is done since 016/017); no supported way to scrub a purged id from *other* players' `pa_*` (needed for P1 MSG-01);
  backup restore before a purge resurrects data (013 runbook must re-run purges); Couchbase tombstones keep keys ~3 days.
- `AccountPurgeService.cs` ~300 lines, split later.
- 011 open: **TLS release gate** (`CouchbaseSettingsIntegrationTests.Tls_…`, opt-in) never run — no TLS cluster locally;
  mandatory before any Live cutover. `run-local.sh` 35 s kill fallback needs bash ≥ 4.4 (stock macOS bash 3.2 only
  graceful stop); not run on macOS/Linux or PowerShell 7. Unity E2E against port 5080 not run. Index `error`/timeout
  paths have no automated test. Ctrl+C before ready reports exit 6. Restricted-user provisioning test adds ~3.7 min.

- 012 open: **owner policy** — Apple verifier accepts only Production receipts in Production; App Store review buys
  with sandbox against prod → rejected (common app-review rejection). Accepting Sandbox there would mark them
  `isTest=true`. Not changed. Manual analyst walkthrough (T062) not done; lock (V4) not run on Linux; re-drive tool
  deferred to ops-readiness (manual N1QL documented); `google:h:` purchases won't match refund records carrying an
  orderId (iap-notifications); deploy manifests not in repo (analytics dir must be a durable mount; K8s: StatefulSet or
  fixed `Observability:InstanceId`); container image needs tzdata (aspnet:10.0 Ubuntu OK, Alpine/chiseled not;
  chiseled-extra OK).

- docs open: 5c checklist sign-off roles and Live replica count need owner/DBA confirmation; VERIFY tags
  (`--contentRoot`, CleanupWindow SDK default, container port/user); Firefox/Safari not tested; `txn_cleanup_lost_total`
  metric expected by requirements but missing.

**Next:** 013 giftcode → 014 iap-notifications → 015 ops-readiness (finishes 5c checklist). Packaging (task 4) is a
separate later effort.
