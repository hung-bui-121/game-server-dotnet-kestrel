# Feature Specification: Consumer readiness — Dev-profile lockdown, consumer test kit, request log, sealed build, reusable local run, version stamping and consumer docs (slice 013)

**Feature Branch**: `013-consumer-readiness`

**Created**: 2026-11-03

**Status**: Draft

**Input**: Owner-approved improvement round (2026-11-03) from the first consumer game `E:\ProjGits\zh-pvp-server` (submodule
`framework/` pinned to tag `v0.6.0`), friction log `docs/framework-feedback/FRICTION_LOG.md` and `UPGRADE.md` of that
repo. Four sequential slices, one tag `v0.7.0` at the end of the round. This slice is group A: **FG-010** (HIGH, security:
Dev-profile/cheat endpoints are accepted outside Production), **FG-001** (framework test helpers are not usable from a game
repo), **FG-006** (no test double for `ICommandContext`), **FG-003** (framework not sealed against a consumer's
`Directory.Build.targets`), **FG-004** (`IsTestProject` decided by the absolute path), **FG-002** (`run-local` hardcoded to
SampleHost), **FG-007** (framework exposes no version), **FG-011** (no error-code band reserved for games), **FG-012**
(command guide written for in-repo modules only), **FG-025** (no per-request/per-command log, host console silent, result
code not exposed to consumer middleware).

## Scope note

- Verified on `main` = `fea3d29` (2026-11-03): every file:line claim of the in-scope friction entries still holds, with
  two precisions (see "Friction claims re-checked" at the end).
- Already in place (P0–012): security profiles `Public`/`Player`/`Gm`/`Dev` on every endpoint with a startup validator
  (SEC-08, ACC-12); Dev endpoints refused at startup **in Production only** (OPS-09, ACC-32); flavors local/dev
  (Development), beta/uat (Staging), live (Production) and `GameServerOptions.AllowsDevelopmentTools` (Development or
  Test), already used to keep test authentication, the GM staff seed, the GM OpenAPI document and fake verifiers out of
  Staging (slice 011, "stricter than OPS-09"); one aggregated startup report (`GameServerStartupException`, `IStartupCheck`,
  exit 1); command route ↔ command checks (`CommandRouteValidator`); `CommandRunner` as the only writer with Touches/Reads
  enforcement inside the internal `CommandContext`; `GameServer.Platform.Testing` with `InMemoryDocumentStore`,
  `FakeClock`, `StoreFaultInjector`, `InMemoryAnalyticsSink` and the file store; `tests/Shared/*.cs` linked into every
  framework test project; compact-JSON file logging per instance through Serilog with a bounded drop queue (LOG-01..06);
  OpenTelemetry traces/metrics with `command_results_total`; correlation id `X-Request-Id`; `tools/local/run-local.ps1|.sh`
  (slice 011, contract `specs/011-local-env/contracts/operations-cli.md` §4); Unity package version `0.6.0` in
  `sdk/unity/com.gameserver.client/package.json`; HTML guides `docs/html/*.html` stamped "Đúng với commit 175df65".
- Gaps this slice closes: a host that registers a cheat module in beta/uat lets every player grant themselves resources
  (three layers only check Production); a game repo cannot reuse the framework's test host, HTTP helpers, local-flavor
  factory or a command-context double; MSBuild applies a consumer's root `Directory.Build.targets` to framework projects and
  treats framework `src` projects as tests when the checkout path contains a `tests` folder; the local run script serves
  only the sample host; no build, log, trace or endpoint states the framework or game version; no error-code band is kept
  for games and the docs example sits in the band the first game chose; the command guide only covers modules inside this
  repo; the framework writes no log line per request or command, its result code is invisible to consumer middleware, and
  the host console prints nothing.
- Not included: route catalog and Unity codegen (slice 014: FG-026 direction B, FG-028, FG-021, FG-027), config validation
  and distribution (slice 015: FG-018, FG-023, FG-024, FG-019), login/command robustness (slice 016: FG-013, FG-015, FG-029,
  FG-016, FG-022); packaging/NuGet (FG-005, FG-008, FG-009) and FG-014, FG-017, FG-030, FG-031 are deferred (see Out of
  Scope).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Cheat endpoints never run outside Development and Test (Priority: P1) — FG-010

An operator deploys a game host to beta, uat or live. Whatever the host code registers by mistake, no Dev-profile
(cheat) endpoint or command can be reached there: the host refuses to start, and even if a Dev endpoint slipped past
startup (route mapped after validation, validator bypassed), the request pipeline and the command runner refuse it. Local
and dev keep their cheats exactly as today.

**Why this priority**: HIGH security finding of the first consumer. Today a host that calls `AddModule<CheatModule>()` in
beta/uat gives every player with a real bearer token a free resource grant; the only protection is one `if` in game code.

**Independent Test**: Start the sample host composition with the cheat module in each flavor (local, dev, beta, uat, live)
and with each deployment environment the options accept; check startup succeeds only for Development/Test. Then build a
Staging host whose Dev endpoint is mapped after startup validation (bypassing the validator) and call it with a valid
player bearer token and with a GM session; check 403 and no state change, no session touch, no analytics resume event.
Call the runner directly with a Dev command in a Staging host; check FORBIDDEN and nothing written.

**Acceptance Scenarios**:

1. **Given** a host whose deployment environment is Staging (flavors beta, uat) or Production (live) or any other value
   that does not allow development tools, **When** it maps any endpoint with profile Dev (e.g. `POST /api/v1/cheat/grant`
   of `CheatModule`), **Then** startup fails before serving traffic with one entry per endpoint in the aggregated startup
   report, naming the endpoint, the deployment environment and environment id, and the fix (register Dev modules only when
   development tools are allowed) (OPS-09, SEC-08, ACC-32).
2. **Given** the same non-development host, **When** a registered module declares a command with profile Dev but no Dev
   endpoint is mapped (module added with `ServiceOnly()`, `Hosting:MapPlayerApi = false`, or a command without route),
   **Then** startup fails as well, naming the module and the command operation.
3. **Given** a non-development host where a Dev endpoint exists at request time anyway (mapped after `UseGameServer`, so
   not seen by the startup validator), **When** a client calls it with a valid player bearer token, a GM session or test
   authentication headers, **Then** the response is 403 `FORBIDDEN` in the usual error contract, no authentication
   handler runs (no session touch, no login/resume analytics event), the handler never runs, and a Warning names the
   route and the environment.
4. **Given** a non-development host, **When** any caller (endpoint, GM flow, job, test) asks the command runner to execute
   or replay a command whose profile is Dev, **Then** the runner refuses with `FORBIDDEN` before the per-player limiter,
   the receipt read and the transaction; nothing is written and the command result metric records the rejection.
5. **Given** a Development or Test host (flavors local, dev; framework test hosts), **When** the cheat module is
   registered and a player or GM actor calls its endpoint, **Then** it works exactly as in `v0.6.0` (either actor type
   accepted; ledger source `cheat`).
6. **Given** an endpoint whose security profile is not one the request pipeline knows explicitly, **When** it is called,
   **Then** it is refused (deny-by-default); no profile falls through to "authenticate as player, else GM".

---

### User Story 2 - A game tests its modules and host with framework test helpers (Priority: P2) — FG-001, FG-006

A game developer, in their own repository with the framework as a submodule, writes (a) unit tests of a command handler
without any host or store, and (b) HTTP smoke tests of their own host on the local flavor, using helpers the framework
publishes for consumers. Nothing in those helpers drags in the framework's sample modules, and the test double behaves
like the real command runner so a handler that passes its unit test does not fail later in the pipeline for an access
rule.

**Why this priority**: Every game hits this on day one (10 + 3 minutes lost and two hand-written substitutes in the first
consumer, plus a sync step on every framework bump).

**Independent Test**: A consumer-shaped test project that references only the framework's consumer test support (no
`tests/Shared` link, no `Sample.*` or `SampleGame` reference) (a) unit-tests a handler with the command-context double:
seeds aggregates, runs the handler, asserts recorded writes/outbox/claims, and gets the same exceptions as the runner for a
write outside Touches, a read outside Touches ∪ Reads and a third claim marker; (b) starts a host entry point on the local
flavor with one call, checks readiness, `/v1/time` and a command round trip, and leaves no file outside a temporary
folder.

**Acceptance Scenarios**:

1. **Given** a game repo with the framework submodule, **When** its test project references the framework's consumer test
   support, **Then** it compiles and runs without referencing any sample module, sample game, or framework test project,
   and without linking files from `framework/tests/` (FG-001).
2. **Given** that test project, **When** it uses the in-process test host with its own module composition, **Then** the
   host starts with the in-memory store (or the file store when selected), a fake clock, test authentication and the
   in-memory analytics sink; the default composition is Identity only.
3. **Given** a host entry point (`Program`) of the game, **When** the test starts it on the local flavor through the
   framework's local-flavor factory, **Then** every setting that would otherwise write into the content root or start
   background work is redirected or disabled (file store folder, outbox sweeper, lease jobs, drain delay, analytics
   directory, operational log directory, distinct instance id), parallel factories never share a folder or an instance id,
   and the folder is deleted when the factory is disposed.
4. **Given** a handler under test, **When** the test builds a command-context double from the command's descriptor and
   the module's aggregate descriptors, sets actor, time, config and seeded states, and runs the handler, **Then** it can
   read the final state of every aggregate written, the outbox entries (including analytics events and GM audit entries
   added through the Core extension methods), and the claim markers with their attributes, in order (FG-006).
5. **Given** the same double, **When** the handler writes an aggregate not in Touches, reads one not in Touches ∪ Reads,
   asks for the wrong state type, exceeds the command's claim-marker limit or claims in `receipts` or a `pa_*` collection,
   **Then** the double fails the same way, with the same message, as the runner's context.
6. **Given** a handler that is run twice on two doubles configured with the same receipt id, **When** it asks for
   `NewId`/`NewSeed` with the same purpose, **Then** the values are identical, as inside the runner for retried attempts.
7. **Given** a handler that commits a business code or a GM command, **When** the test asks the double (or a companion
   check) whether the outcome would be committable by the runner, **Then** an uncatalogued commit code, a non-200 commit
   code, a GM command without exactly one audit entry or a non-GM command with an audit entry is reported.

---

### User Story 3 - Developers and operators see every request; consumer middleware reads its result (Priority: P3) — FG-025

A client developer runs the local server (`run-local`, or the server window of the Unity Editor) and sees one line per
request on the console: route, operation, actor, request id, HTTP status, result code, replayed flag and duration. In any
environment the operational log holds the same event as structured data (LOG-04). A game that needs its own per-request
behaviour (metrics, audit, a different sink) reads the framework's result for the request instead of parsing response
bodies.

**Why this priority**: The first consumer spent 45 minutes and maintains nine files (body capture, envelope parsing,
console sink) to get one console line per request; LOG-04 already requires action, duration and error code in the log.

**Independent Test**: Run a scripted session against a test host (guest login, snapshot, config get and not-modified,
command OK, business rejection at HTTP 200, replayed command, validation error 400, auth failure 401, unknown route 404,
GM request, client abort, injected 500, health probes); check exactly one request event per non-health request with the
expected fields and codes, none for health probes, no secret in any event, and that a test middleware placed before the
framework pipeline reads the same code for each request. Start the sample host on the local flavor and check the console
shows one line per request; block the log sink and check requests are not slowed.

**Acceptance Scenarios**:

1. **Given** any request the host answers except health probes, **When** the response completes (or the client aborts),
   **Then** exactly one structured request event is written with: HTTP method, route template (never the raw path with
   ids or the query string), security profile, command operation when the route runs a command, actor type and id when
   authenticated, request/correlation id, trace id, HTTP status, result code, replayed flag, config not-modified flag,
   and duration in milliseconds (LOG-04, OBS-02).
2. **Given** a command rejected with a business code at HTTP 200, a command committed with a code, a replayed command, an
   error envelope or a GM Problem Details response, **When** the event is written, **Then** its result code is exactly the
   code the client received (`OK` when none), and `replayed`/`not_modified` match the response.
3. **Given** a request that failed before reaching an endpoint (gate maintenance, auth failure, 404, 405, payload too
   large, invalid Protobuf), **When** it completes, **Then** it still produces one event with the error code written.
4. **Given** a consumer middleware placed outside the framework pipeline, **When** it runs after the request, **Then** it
   can read the request's outcome (status, result code, operation, replayed, not-modified) from a public, documented
   per-request feature without reading or buffering the response body; the same outcome is available for non-command
   routes and for rejections, which the `afterExecute` hook of `MapPlayerCommand` does not cover.
5. **Given** the sample host started with `run-local` on the local flavor, **When** requests arrive, **Then** each prints
   one human-readable line on the host console, and other operational events at or above the configured level appear
   there too; the file log is unchanged in content.
6. **Given** the console or file sink cannot keep up, **When** requests continue, **Then** request latency is unaffected,
   events are dropped and counted (LOG-06), and no request fails because of logging.
7. **Given** a login request, a request authenticated by test headers, or a GM login, **When** its event is written,
   **Then** it contains no token, password, guest secret, provider token, cookie, CSRF token, header value or body
   (LOG-05); an actor id taken from test authentication headers is sanitized and length-limited.

---

### User Story 4 - The framework builds the same inside any consumer repository (Priority: P3) — FG-003, FG-004

A game team places the framework as a submodule anywhere (including under a folder named `tests`) and keeps its own
root build files. Framework projects build exactly as in the framework repo: the consumer's `Directory.Build.targets`
does not leak into them, and the framework decides which of its own projects are tests from their place inside the
framework, not from the absolute path of the checkout.

**Why this priority**: Silent build corruption (CS0246 `Xunit` in `GameServer.Core`, analyzers and banned APIs switched
off) and a rule every consumer must remember; small fix, needed before more consumers arrive.

**Independent Test**: Copy or check out the framework under a temporary parent folder named `tests` whose root contains a
`Directory.Build.targets` that fails the build (or prints a marker); build every framework project in Release; check 0
errors, 0 warnings, no marker, banned-API analyzers active on `src` projects, and `IsTestProject` true only for projects
under the framework's own `tests/` folder.

**Acceptance Scenarios**:

1. **Given** a consumer repo whose root has a `Directory.Build.targets`, **When** a framework project inside the
   submodule is built (directly or as a project reference of a game project), **Then** nothing from that file is applied
   to the framework project (FG-003).
2. **Given** a framework checkout whose absolute path contains a folder named `tests` above the framework root, **When**
   framework `src`, `modules`, `samples`, `sdk`, `templates` or `tools` projects are built, **Then** they are not test
   projects: no global `using Xunit`, analyzers and banned APIs are on, and the build succeeds (FG-004).
3. **Given** the framework's own test projects under `tests/`, **When** they are built from any checkout path, **Then**
   they are still test projects with the current behaviour.

---

### User Story 5 - A game runs its own host locally with the framework script (Priority: P3) — FG-002

A game developer starts the game host on the local flavor with the framework's `run-local` script from the submodule,
passing the host project and launch profile, instead of maintaining a copy of the script. The script reads the port, the
.NET SDK version, the log folder and the data folder from the host's own files.

**Why this priority**: A 200-line copy per game (PowerShell and shell) that must be re-synced on every bump.

**Independent Test**: From a fixture consumer layout (a second host project outside `samples/SampleHost`), run
`run-local.ps1` and `run-local.sh` with the host project, launch profile and name parameters; check ready, the banner
shows the given name and the host's URL, `-Reset` empties that host's data folder, a failure before ready tails that
host's log folder; run both scripts with no parameter in the framework repo and check behaviour and exit codes are those
of slice 011.

**Acceptance Scenarios**:

1. **Given** the framework repo, **When** `run-local` runs with no new parameter, **Then** it behaves exactly as the
   slice 011 contract (SampleHost, profile `local`, port 5080, exit codes 0 and 2–7, same banner).
2. **Given** a consumer repo, **When** `framework/tools/local/run-local.ps1` (or `.sh`) runs with a host project path, a
   launch profile name and a display name, **Then** it builds and starts that host from its project folder, the default
   port is the one in that launch profile, the required SDK version is the one of the `global.json` governing the host,
   and the log and data folders used for messages, `-Reset` and the failure tail come from the host's flavor settings.
3. **Given** a host project path that does not exist, a launch profile the host does not define, or a host without the
   settings the script needs, **When** the script starts, **Then** it exits with the invalid-argument code (2) and a
   message naming what is missing and how to fix it; it never edits a file of the consumer.
4. **Given** the banner of a non-sample host, **When** it is ready, **Then** it shows only what applies to that host
   (name, base URL, health, LAN warning); sample-only hints such as the development GM staff password are not printed for
   other hosts.

---

### User Story 6 - Everyone knows which framework and game version are running (Priority: P4) — FG-007

A developer, operator or support engineer can tell, from the build output, the startup log, telemetry and an
authenticated ops endpoint, which framework version and which game version a running instance is built from.

**Why this priority**: Needed to correlate incidents and bumps across games; low effort, low risk.

**Independent Test**: Build the framework and the sample host; check every framework assembly carries the declared
framework version and it equals the Unity package version; start the sample host and check the startup log line, the
telemetry resource attributes and the GM ops version response; build the framework as a project reference of a
consumer-shaped host and check the same framework version appears.

**Acceptance Scenarios**:

1. **Given** the framework build files declare version `X.Y.Z`, **When** any framework project is built (in the framework
   repo or as a project reference from a consumer), **Then** its assemblies carry `X.Y.Z` and an informational version
   that also identifies the source revision when available (ARC-08).
2. **Given** the declared framework version and the Unity package version differ, **When** the framework test suite
   runs, **Then** a test fails naming both values.
3. **Given** a host starts, **When** it is about to serve, **Then** one Information line states the game id, the game
   (entry assembly) version, the framework version, the environment id and the instance id.
4. **Given** telemetry is exported, **When** a span or metric is read, **Then** the resource carries `service.version` =
   the game (entry assembly) version and a separate attribute with the framework version.
5. **Given** a GM staff member with the ops read permission, **When** they read the ops version information, **Then**
   they get framework version, game version, environment id and instance id; unauthenticated callers and public health
   probes get no version information.

---

### User Story 7 - A game follows one consumer guide and picks error codes that never collide (Priority: P4) — FG-011, FG-012

A game developer who has never seen the framework repo reads the consumer guide and the updated "add a command" page and
sets up their repo: submodule pinned to a tag, build files, project references, host composition, tests with the
consumer test kit, local run, error codes in the band reserved for games, and the bump procedure. Every page says which
framework tag it matches.

**Why this priority**: Documentation of what the other stories deliver; it must come last so it describes the shipped
behaviour.

**Independent Test**: A reviewer, using only the consumer guide and the updated pages, creates a fixture consumer repo
with the framework at the slice commit, adds one module with one command and its error codes, one handler unit test with
the command-context double and one local-flavor smoke test, runs the host with `run-local`, and gets everything green
without reading framework source; the reviewer records every step that was missing or wrong.

**Acceptance Scenarios**:

1. **Given** the module-authoring guide and the "add a command" page, **When** a game developer chooses an error-code
   range, **Then** the docs state a band reserved for game modules (50000–89999) that no framework, built-in, sample or
   template module uses, and no example in the docs uses a range inside that band (FG-011).
2. **Given** the framework adds or changes a framework-owned module, **When** its error-code range falls in the game band,
   **Then** a framework test fails naming the module and the range.
3. **Given** the consumer guide, **When** a game developer follows it, **Then** it covers: submodule pinned to an
   annotated tag (never a branch), the build files a game mirrors and those the framework now seals (FG-003, FG-004; the
   manual sync of FG-008 stays documented), referencing framework projects, the host composition (copied from SampleHost
   until FG-009), tests with the consumer test kit and the command-context double, `run-local` with host parameters,
   version reporting, the request log and console, error-code band, and the bump procedure; Unity code generation for the
   game's own `.proto` is marked as coming in slice 014 (FG-012).
4. **Given** the "add a command" HTML page (Vietnamese), **When** a reader reaches host registration and tests, **Then**
   each step shows both the in-repo variant (module inside this repo, `GameServer.slnx`, `tests/Shared`) and the
   consumer-repo variant (module in a game repo, consumer test kit), and the page states the framework tag it matches
   instead of a bare commit.

---

### Edge Cases

- Deployment environment value other than Development, Test, Staging and Production (allowed by configuration today
  only through tests or custom hosts): development tools are not allowed, so Dev endpoints and commands fail startup.
- `TestHost` (environment id `test`) switched to Staging or Production by a test while composing the full sample game,
  which includes `CheatModule`: startup now fails; framework test compositions must add the cheat module only when
  development tools are allowed (the same rule the sample host follows).
- `--provision` mode of a host whose composition registers a Dev module in Staging/Production: it fails the same way as a
  serving start, because the composition is invalid for that environment.
- A Dev command reached through a route of another profile: already a startup failure (route profile ≠ command profile,
  `CommandRouteValidator`); the runner refusal (FR-005) still covers routes mapped after `UseGameServer`.
- A receipt committed by a Dev command earlier in a development store and replayed after the same store is opened by a
  non-development host: refused by the runner's replay path (no replay of Dev results outside development).
- A command-context double built without the module's aggregate descriptors: building it fails with a message naming the
  missing aggregate, instead of returning null or a blank state.
- Handler that calls `Set` and then `Get` on the same aggregate in the double: returns the new state, as the runner's
  context does.
- Handler that calls `TryClaimAsync` for a marker the test seeded as already present: returns false and records nothing.
- Local-flavor factory used for a host whose flavor file points the store, analytics or log folder elsewhere: the factory
  settings win; nothing is written outside the temporary folder.
- Two local-flavor factories in parallel in one test run: distinct folders and instance ids (no analytics lock clash, no
  log file sharing).
- Request aborted by the client mid-command: the event reports the abort (no status sent) and the command's own outcome
  is whatever the runner did (commit may still happen, PERF-02); the log never claims a commit it did not see.
- Request answered by a consumer endpoint mapped outside the framework helpers: still one event; the result code is `OK`
  for 2xx without a framework code and the HTTP status otherwise, unless the consumer sets the outcome itself.
- Health probes polled every second by the Unity server window: never logged as request events.
- Very large responses (config bundle): the request log never buffers or copies response bodies.
- Unmatched paths (scanners): the route field holds a cleaned, length-limited path, so log lines cannot be inflated or
  carry control characters.
- Console sink enabled while stdout is redirected to a file or a pipe that stops reading: events are dropped and counted,
  requests are not blocked.
- Framework checkout path with `tests` both above and inside the framework root (e.g. `D:\tests\game\framework\tests\X`):
  only the inner `tests/` decides.
- Consumer root `Directory.Build.props`: already not applied to framework projects (the framework has its own, which does
  not import parents); unchanged.
- `run-local` invoked from a directory other than the consumer root: relative host paths resolve against the current
  directory; the script prints the resolved project path.
- `run-local` with a launch profile whose URL has no port or several URLs: the first http URL's port is used; none →
  default 5080 and a note in the banner.
- Version declared in the build files changed without updating the Unity package (or the reverse): the version test fails
  before merge.
- Version of a host built from a framework commit between tags: the informational version shows the revision, so it is
  not mistaken for the released tag.

## Requirements *(mandatory)*

### Functional Requirements

**Dev profile only where development tools are allowed (FG-010; OPS-09, SEC-08, ACC-12, ACC-32, constitution III)**

- **FR-001** (FG-010, SEC-08, OPS-09): The startup endpoint validation MUST report every endpoint with profile Dev when
  the host does not allow development tools (deployment environment other than Development or Test, i.e. every Staging
  and Production flavor and any other value). The message names the endpoint (method and route), the deployment
  environment, the environment id and the fix. Reported in the one aggregated startup report; the host exits with code 1.
- **FR-002** (FG-010, OPS-09): Module registration validation MUST report every registered command whose descriptor
  profile is Dev when the host does not allow development tools, naming module and operation, whether or not a Dev
  endpoint is mapped (service-only modules, player API not mapped, commands without route). Applies wherever the module
  registry is validated, including `--provision`.
- **FR-003** (FG-010, SEC-08): The request pipeline MUST refuse a request to a Dev-profile endpoint with 403 `FORBIDDEN`
  in the standard error contract when the host does not allow development tools, before any authentication scheme is
  invoked (no session touch, no resume analytics event, no bearer or cookie validation) and before CSRF, permission or
  body handling; it logs a Warning naming method, route and environment (no header or token value).
- **FR-004** (FG-010, SEC-08 deny-by-default): The request pipeline MUST handle each security profile explicitly; a
  profile value it does not know is refused with 403 `FORBIDDEN`. No profile is authenticated through a fallback branch.
  In Development/Test, Dev endpoints keep today's behaviour (player schemes, then GM schemes).
- **FR-005** (FG-010, SEC-07, OPS-09): The command runner MUST refuse a Dev-profile command with `FORBIDDEN` for every
  actor type when the host does not allow development tools, on the execute path and on the replay path
  (`TryReplayAsync` included), before the per-player limiter, receipt read, guards and transaction; nothing is written,
  the command-result metric counts the rejection. In Development/Test, Dev commands accept player and GM actors as today.
- **FR-006** (FG-010): All three layers (FR-001/FR-002, FR-003/FR-004, FR-005) MUST use the same decision
  (`AllowsDevelopmentTools` of the deployment options); no setting enables Dev endpoints or commands outside Development
  and Test in this slice. The deferred Staging cheat switch (`GameServer:Staging:AllowCheatModule`, PCR platform §17, R13)
  stays deferred and, if added later, must be an explicit extension of this decision, never Production.
- **FR-007** (FG-010, constitution VI): Each layer MUST have its own automated test that proves it refuses Dev with the
  other layers out of the way (validator alone; middleware with the validator bypassed; runner called directly), plus a
  flavor matrix test (local, dev start with the cheat module; beta, uat, live fail).
- **FR-008** (FG-010, PCR-01): The PCR platform (security profiles, flavors, runner rules), the environments guide, the
  module-authoring guide and the HTML pages that say "Dev — forbidden in Production" MUST state the new rule: Dev only
  where development tools are allowed (local, dev, tests), stricter than OPS-09; the breaking change is recorded (see
  "Breaking changes").

**Consumer test kit (FG-001; TST-01, constitution VI)**

- **FR-010** (FG-001): The framework MUST provide consumer test support (one or more projects referenced by path from a
  submodule checkout, packaged later with FG-005/NuGet) that depends on no sample module (`Sample.Counter`, `SampleGame`),
  no framework test project and no file under `tests/`. It does not force a unit-test framework on the consumer.
- **FR-011** (FG-001): The consumer test support MUST contain at least: Protobuf HTTP helpers (post/parse, request ids),
  identity helpers (guest login and bearer client), store selection helpers (in-memory or file store, temporary folders,
  `GAMESERVER_TEST_STORE` honoured), GM browser helpers working on a `TestServer` (cookie jar, CSRF refresh, local staff
  login), and an in-process test host whose module composition is chosen by the caller, with the in-memory store, a fake
  clock, test authentication, the in-memory analytics sink and captured logs; its default composition is Identity only.
- **FR-012** (FG-001): The consumer test support MUST provide a local-flavor host factory for any host entry point that
  applies, to one per-instance temporary folder, every setting needed to run the local flavor in tests without touching
  the content root or starting background work: `FileStore:Folder`, `GameServer:Outbox:SweeperEnabled`,
  `GameServer:Jobs:Enabled`, `GameServer:Hosting:DrainDelaySeconds`, `GameServer:Analytics:Directory`,
  `Observability:InstanceId` (unique per factory) and `Observability:Logging:Directory`; lets the test add or override
  settings and opt into the sweeper; deletes the folder on dispose. The list is documented, and a new framework setting
  that writes to disk or starts background work is added to it in the slice that introduces the setting.
- **FR-013** (FG-001): The framework's own test suite MUST use the consumer test support for the parts it covers (the
  shared test host built on it, `SampleHostSmokeTests` on the local-flavor factory), so the consumer surface is exercised
  by every framework build; sample-specific helpers (`GameHttp`, full sample composition, Counter defaults) stay with the
  framework tests.
- **FR-014** (FG-001, FG-005 seam): The public surface of the consumer test support SHOULD not expose file-store
  internals, so moving the local stores out of `GameServer.Platform.Testing` later (FG-005) changes at most namespaces or
  references in consumer tests.

**Command-context test double (FG-006; DAT-04, DAT-09, DAT-11, CMD-04)**

- **FR-020** (FG-006): The consumer test support MUST include a public test double of `ICommandContext` usable without a
  host, store or dependency injection.
- **FR-021** (FG-006): The double MUST be built from the command's descriptor and the aggregate descriptors of the
  registered modules (so a missing aggregate returns the descriptor's default state and state types are checked like the
  runner) and MUST let the test set: actor (player id, game id, environment id, actor type, GM permissions), the fixed
  `Now`, the game config (`IGameConfig` test double with values and config version), the receipt id used for derived ids,
  seeded aggregate states and pre-existing claim markers.
- **FR-022** (FG-006): The double MUST record, in call order and readable after the handler ran: the latest state of each
  aggregate set, every outbox entry (kind and payload, including entries added through the Core analytics and audit
  extension methods) and every claim marker (collection, key, attributes).
- **FR-023** (FG-006, DAT-09, DAT-11, DAT-04): The double MUST enforce the same rules as the runner's context with the same
  exception types and messages: `Set` outside Touches, `Get`/`Set` outside Touches ∪ Reads, state type mismatch, more
  claim markers than the command's limit, claim markers in `receipts` or `pa_*` collections, duplicate claim in one run
  returns false, pre-existing marker returns false.
- **FR-024** (FG-006, CMD-04, PLY-05): `NewId` and `NewSeed` of the double MUST derive from the configured receipt id and
  the purpose exactly as the runner does, so values are stable across runs and equal to the runner's for the same receipt
  id.
- **FR-025** (FG-006): The double and the runner's context MUST NOT drift: either they share the rule implementation or a
  conformance test runs the same handlers and violations through both and compares results; a rule added to the runner's
  context without the double fails that test.
- **FR-026** (FG-006, SEC-14): The consumer test support MUST offer a check of the committed outcome against the runner's
  commit rules: commit code catalogued by a registered module and HTTP 200, exactly one audit entry for a GM command and
  none for other profiles, Touches/claim limits of the descriptor.

**Build sealed against consumers (FG-003, FG-004)**

- **FR-030** (FG-003): The framework root MUST contain a `Directory.Build.targets` so MSBuild applies no
  `Directory.Build.targets` from folders above the framework root to framework projects. It adds no behaviour of its own
  beyond what the framework needs.
- **FR-031** (FG-004): Whether a framework project is a test project MUST be decided from its location relative to the
  framework root (projects under the framework's `tests/` folder) or by another rule independent of the absolute checkout
  path; analyzers and banned APIs apply to every other framework project from any checkout path.
- **FR-032** (FG-003, FG-004): An automated check (test or CI step, documented) MUST build or evaluate framework projects
  from a location whose parent path contains a folder named `tests` and a hostile `Directory.Build.targets`, and fail if
  either leaks.
- **FR-033** (FG-003, FG-008 seam): The consumer guide MUST list which build inputs are now sealed (`Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props`, `.editorconfig` with `root = true`) and which still come from the
  consumer and need the manual sync of FG-008 (`global.json` of the consumer root, `nuget.config` hierarchy).

**Reusable local run (FG-002; OPS-02, slice 011 operations contract)**

- **FR-040** (FG-002): `run-local.ps1` and `run-local.sh` MUST accept, with the same meaning in both, a host project
  (path to the project file or its folder), a launch profile name and a display name, in addition to the slice 011
  parameters (port, reset, LAN).
- **FR-041** (FG-002): Without the new parameters both scripts MUST behave exactly as the slice 011 contract (SampleHost,
  profile `local`, port 5080, exit codes 0 and 2–7, banner). Slice 011 verified the scripts manually (no automated
  script test exists); this slice adds a repeatable check (automated where the platform allows, else a documented manual
  run) for the default invocation and for a second host.
- **FR-042** (FG-002): With a host project, the default port MUST be the port of the first http URL of the selected launch
  profile (5080 when there is none); `-Port` still overrides.
- **FR-043** (FG-002): The required SDK version shown in messages MUST be read from the `global.json` that governs the
  host project (nearest upward), never hardcoded.
- **FR-044** (FG-002): The data folder (messages, `-Reset`) and the log folder (failure tail) MUST be read from the host's
  settings for the flavor of the selected profile (base settings plus the flavor file: `FileStore:Folder`,
  `Observability:Logging:Directory`), resolved against the host's content root.
- **FR-045** (FG-002): Invalid host project, unknown launch profile or unreadable host settings MUST exit with code 2 and a
  message naming the problem and the fix; the scripts never modify consumer files and print the resolved host project.
  New exit codes, if any, are added after 7 and documented.
- **FR-046** (FG-002): The banner MUST use the display name and print only host-independent lines (base URL, health,
  LAN warning) for non-sample hosts; sample-only hints are kept for the sample host. The operations contract of slice 011
  (`specs/011-local-env/contracts/operations-cli.md` §4) is superseded by an updated contract in this slice, and the
  environments guide documents the consumer invocation.

**Version (FG-007; ARC-08, OBS-04)**

- **FR-050** (FG-007, ARC-08): The framework MUST declare one SemVer version in its own build files, stamped into every
  framework assembly (assembly, file and informational versions; the informational version adds the source revision when
  available), also when framework projects are built as project references from a consumer repository.
- **FR-051** (FG-007, ARC-08): The declared version MUST equal the version of the Unity package
  (`sdk/unity/com.gameserver.client/package.json`); a framework test fails on mismatch. The release procedure states that
  the annotated tag is `v{version}`.
- **FR-052** (FG-007): The framework MUST expose its version and the host's entry-assembly (game) version through a public
  API, so a consumer can log or compare them without its own assembly attributes.
- **FR-053** (FG-007, OBS-04): The telemetry resource MUST carry `service.version` = the game (entry assembly) version and a
  separate attribute with the framework version.
- **FR-054** (FG-007, LOG-04): At startup, before serving, the host MUST log one Information line with game id, game
  version, framework version, environment id and instance id.
- **FR-055** (FG-007, SEC-07): An authenticated GM ops read (existing `ops.read` permission) MUST return framework version,
  game version, environment id and instance id; public endpoints and health probes do not disclose versions. The GM
  OpenAPI document and PCR gm list it.

**Error-code band for games (FG-011; API-05, API-11)**

- **FR-060** (FG-011): The range 50000–89999 MUST be reserved for game (consumer) modules and documented in the
  module-authoring guide, the PCR platform error-code section and the "add a command" page (§4.2), next to the ranges
  already used by the framework (core 0–999, sample.counter 10000–10099, economy 11000–11099, iap 12000–12099, gm
  13000–13099, identity 14000–14099, sample.game 40000–40099, template 90000–90099).
- **FR-061** (FG-011): No documentation example MUST use a range inside the game band; the module-authoring example moves
  out of 50000–50099.
- **FR-062** (FG-011): An architecture test MUST fail when a framework-owned module (core, built-in, sample or template)
  declares an error-code range overlapping the game band.

**Consumer documentation (FG-012; PCR-01, docs DoD of slice docs-html-guides)**

- **FR-070** (FG-012): A consumer guide (Markdown in `docs/guides/`) MUST describe using the framework from a game
  repository: submodule pinned to an annotated tag (no branch, no `--remote`), build files (what the game mirrors, what is
  sealed, FR-033), Central Package Management and pinned versions, project references to framework projects, host
  composition (copy of SampleHost until FG-009), consumer test kit and command-context double (FR-010..FR-026),
  `run-local` with host parameters (FR-040..FR-046), version reporting (FR-050..FR-055), request log and console
  (FR-080..FR-089), the game error-code band (FR-060), Unity SDK by UPM URL with the tag, and the bump checklist; game
  `.proto` code generation for Unity is marked "slice 014".
- **FR-071** (FG-012): `docs/html/02-them-command.html` (Vietnamese) MUST give, for host registration (§4.6) and tests
  (§4.8, §5 step 7), both the in-repo variant and the consumer-repo variant, link the consumer guide, and include a handler
  unit test with the command-context double; `docs/guides/module-authoring.md` §8 and `templates/module/README.md` are
  updated accordingly.
- **FR-072** (FG-012): Every HTML page and guide touched by this slice MUST state the framework tag or version it matches
  (`v0.7.0` once tagged; until then the declared version with "unreleased"), not only a commit; pages not touched keep
  their stamp and are listed as "not re-checked" in the release notes.
- **FR-073** (FG-012, docs DoD): The HTML pages MUST reflect the pipeline changes of this slice (Dev refusal in the
  profile step, the request log in the pipeline walkthrough, the console line in the local-run step).

**Request log, result outcome and console (FG-025; LOG-04, LOG-05, LOG-06, OBS-02, constitution VII)**

- **FR-080** (FG-025, LOG-04): The framework MUST write exactly one structured request event per HTTP request the host
  answers or that the client aborts, for every route (player, GM, public, Dev; commands, queries, login, snapshot, config;
  errors including 404/405), except the health probe routes and other routes in a configurable exclusion list. The
  event uses its own log category so it can be filtered or levelled independently.
- **FR-081** (FG-025, LOG-04, OBS-02): The request event MUST contain: HTTP method, route template (for unmatched requests a
  cleaned, length-limited path), security profile, command operation (when any), actor type and actor id (player id or
  staff id, when authenticated), request/correlation id, trace id, HTTP status (none when aborted), result code, replayed,
  config not-modified, duration in milliseconds; plus the enrichment every operational event already has (game id,
  environment id, instance id). Field names are fixed in the plan and PCR.
- **FR-082** (FG-025, LOG-05): The request event MUST NOT contain bodies, query strings, header values, cookies, tokens,
  passwords, guest secrets, provider tokens, CSRF tokens or purchase tokens; actor ids that come from test-authentication
  headers are sanitized and length-limited.
- **FR-083** (FG-025, API-11): The result code MUST be the code the client received: `OK` for success without code; the
  business code of a command response (committed with a code or rejected at HTTP 200); the error-envelope or Problem
  Details code for errors; a distinct value for client aborts; the internal-error code for unhandled failures. The
  command-result metric and the request event agree for command routes.
- **FR-084** (FG-025): The framework MUST record the request outcome (status, result code, operation, replayed,
  not-modified) in a public, documented per-request feature, set for every response the framework writes (commands,
  rejections, errors, config, other framework routes), readable by consumer middleware placed before the framework
  pipeline after the request completes and by consumer endpoints/hooks; a consumer endpoint may set it for its own
  responses. Reading it never requires buffering or parsing the response body.
- **FR-085** (FG-025, LOG-06, PERF-01): Request logging MUST NOT block, delay measurably or fail a request: events go through
  the bounded, dropping operational log pipeline, drops are counted, and a logging failure never changes the response.
- **FR-086** (FG-025, OPS-01): Request logging MUST be configurable per deployment (on/off, level through the existing level
  overrides, exclusion list) and validated at startup. Default: **on in Development flavors (local, dev) and Test, off in
  Staging/Production (beta, uat, live)** until operations enable it explicitly by configuration; the environments guide
  and PCR state the volume estimate (~300–400 bytes per event, about 13–17 GB per instance per day at 500 requests/s).
  (Decision: orchestrator default 2026-11-03, owner may override.)
- **FR-087** (FG-025, LOG-06): The framework MUST offer an optional console sink in the observability logging settings:
  one human-readable line per event (request events and other operational events at or above its own minimum level),
  bounded and dropping like the file sink (never the default console provider that waits when its queue is full), off by
  default; the sample host's local and dev flavor files turn it on. Enabling it in Staging/Production is allowed and logged
  at startup.
- **FR-088** (FG-025, LOG-01..LOG-03): The file sink keeps receiving every event, in the same compact JSON format; the
  console sink is in addition to files, never instead of them, and operational logs still go to no other provider.
- **FR-089** (FG-025, PCR-01): The PCR platform MUST record the request event (fields, levels, exclusions, accepted loss),
  the outcome feature (semantic contract with consumer code) and the console setting; the environments guide lists the
  settings per flavor and the expected log volume.

**Release and cross-cutting (ARC-08, PCR-01, constitution VI)**

- **FR-090** (ARC-08): Release notes for this slice (carried into the `v0.7.0` tag message) MUST list the fixed FG ids,
  every breaking change with its fix, and the consumer workarounds that become removable (see "Consumer impact").
- **FR-091** (constitution VI): Build of `GameServer.slnx` in Debug and Release with 0 warnings and the full test suite
  including Couchbase MUST pass; every acceptance scenario above maps to an automated test or a documented manual check
  (manual: the consumer-guide walkthrough of story 7, `run-local.sh` on non-Windows if no runner is available).

### Key Entities

- **Development tools allowance**: the single decision (deployment environment Development or Test) that gates Dev
  endpoints, Dev commands, test authentication, the GM staff seed, the GM OpenAPI document and fake verifiers.
- **Consumer test kit**: framework-provided test support for game repositories: test host with caller-chosen modules,
  HTTP/identity/store/GM helpers, local-flavor host factory, command-context double, outcome checks.
- **Command-context double**: in-memory `ICommandContext` built from command and aggregate descriptors; seeded states and
  claims; records writes, outbox entries and claims; enforces the runner's access rules.
- **Local-flavor settings set**: the documented list of settings a test must redirect or disable to run a host on the
  local flavor without side effects.
- **Request event**: one structured log event per request with route, operation, actor, ids, status, result code,
  replayed, not-modified and duration.
- **Request outcome**: the per-request record of status, result code, operation, replayed and not-modified that the
  framework sets and consumer code reads.
- **Console sink**: optional, bounded, one-line-per-event console output of the operational log.
- **Framework version**: SemVer declared once in the framework build files, equal to the Unity package version, stamped
  into assemblies and reported by log, telemetry and the GM ops read.
- **Game error-code band**: 50000–89999, reserved for consumer modules.
- **Consumer guide**: documentation for using the framework from a game repository.

## Breaking changes (to existing consumers of `v0.6.0`)

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Dev-profile endpoints and commands are refused outside Development/Test (was: Production only) (FR-001, FR-002) | Hosts that register a cheat/Dev module in beta, uat or any Staging environment; framework tests that run the full sample composition in Staging | Startup fails with exit 1; the aggregated report has one line per Dev endpoint and per Dev command naming environment and fix; release notes and PCR "Breaking" | Register Dev modules only when `AllowsDevelopmentTools` (as SampleHost and the first consumer already do) |
| BC-2 | A Dev endpoint reached at request time outside Development/Test answers 403 `FORBIDDEN`; the runner refuses Dev commands there (FR-003, FR-005) | Hosts that map Dev routes after `UseGameServer` in Staging, or call Dev commands from jobs/GM flows in Staging | 403 in the error contract plus a Warning log; `FORBIDDEN` from the runner; release notes | Same as BC-1 |
| BC-3 | Startup message text for Dev endpoints changes (was `uses profile Dev in Production`) | Tests that match the old message | Test failure in the consumer suite; old and new text in release notes | Match the new text or the endpoint label |
| BC-4 | `tests/Shared` is reorganized; `ProtobufHttp.cs`, `IdentityHttp.cs`, `TestStores.cs` may move into the consumer test kit (FR-011, FR-013) | Consumers that link those files by path (the first consumer does) | Build error in the consumer test project after the bump; release notes name each moved file and its replacement | Reference the consumer test kit instead of linking files |
| BC-5 | A consumer root `Directory.Build.targets` no longer applies to framework projects; framework test-project detection is relative to the framework root (FR-030, FR-031) | Consumers that relied, knowingly or not, on injecting targets into framework projects | Release notes | Move such settings to the consumer's own projects |
| BC-6 | Framework assemblies carry the declared version instead of 1.0.0.0; telemetry `service.version` appears (FR-050, FR-053) | Dashboards or tooling keyed on version 1.0.0.0 or on the absence of `service.version` | Release notes | Update dashboards |
| BC-7 | One new log event per request (and optional console output) increases operational log volume (FR-080, FR-086) | Deployments with tight log disk or shipping budgets | Release notes with the volume estimate; environments guide | Tune the level/exclusions or disable per FR-086 |

No player API wire contract, persisted document format or GM API response changes, except the new GM ops version read
(additive).

## Consumer impact / workaround removal (zh-pvp-server after `v0.7.0`)

From the friction log entries and the `FRAMEWORK-GAP` markers found in the game repo on 2026-11-03. The game session owns
the removal; this slice only makes it possible and lists it in the release notes (FR-090).

| FG | Marker / workaround today (game repo) | Removable after `v0.7.0` | Stays |
| --- | --- | --- | --- |
| FG-010 | `src/ZhPvp.Host/ZhPvpModules.cs` marker on the `AllowsDevelopmentTools` condition; `tests/ZhPvp.Tests/Host/CompositionTests.cs` marker (test as the only protection) | Both markers (framework refuses Dev in beta/uat itself) | The condition and the 5-flavor composition test may stay as game hygiene; CLAUDE.md rule 7 |
| FG-001 | `tests/ZhPvp.Tests/ZhPvp.Tests.csproj` links `ProtobufHttp.cs`, `IdentityHttp.cs`, `TestStores.cs` from `framework/tests/Shared`; `tests/ZhPvp.Tests/Support/LocalFlavorHost.cs` mirrors the smoke-test settings (+ log folder); `UPGRADE.md` step 2 diff of `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` and `tests/Shared` | Links, `LocalFlavorHost`, both markers and that diff line of `UPGRADE.md` (replaced by the consumer test kit and the local-flavor factory) | The flavor-file part of that `UPGRADE.md` diff line (`samples/SampleHost/appsettings*.json`, `.gitattributes`) until FG-009 |
| FG-006 | `tests/ZhPvp.Tests/Support/FakeCommandContext.cs` (Get/Set only, others throw) | The file and its marker (framework double) | — |
| FG-003 | Marker and comment in `Directory.Build.props`; rule 3 of `CLAUDE.md` ("never add a root `Directory.Build.targets`") | Marker and rule; the game may add its own root targets file | — |
| FG-004 | Target `ZhPvpRefuseTestsAncestor` and marker in `Directory.Build.props`; notes in `CLAUDE.md`, `README.md`, `docs/KNOWN_LIMITATIONS.md` | Target, marker and notes | The game's own name-based `IsTestProject` rule (game choice) |
| FG-002 | `tools/local/run-local.ps1`, `tools/local/run-local.sh` (copies with markers); `UPGRADE.md` step 2 diff of `tools/local` | The copies (call `framework/tools/local/run-local.* -HostProject src/ZhPvp.Host …`) and that diff | Game-only pre-checks (submodule initialized, exit 8; checked-out tag = props tag) as a thin wrapper around the framework script, if the game keeps them |
| FG-007 | `src/ZhPvp.Host/BuildInfo.cs`, `AssemblyMetadata("FrameworkTag")` in `src/ZhPvp.Host/ZhPvp.Host.csproj`, startup log in `Program.cs` | Framework-tag stamping and the hand-written startup line (framework logs it, FR-054; version API, FR-052) | `<Version>` of the game; `GameServerFrameworkTag` in props for the "checked-out tag = pinned tag" test (now comparable with the framework's declared version) |
| FG-011 | Band 50000–50999 recorded only in `CLAUDE.md`, `docs/pcr/placeholder.md` | Nothing to remove; the band is inside the reserved game band, so the note can point to the framework docs | Game's per-module sub-ranges (game choice) |
| FG-012 | No marker; consumer steps written in `docs/COMMANDS.md`, `UPGRADE.md` | Duplicated consumer steps can point to the framework consumer guide | Game-specific steps |
| FG-025 | `src/ZhPvp.Host/RequestLog/*.cs` (middleware, body capture, envelope parsing, sink, startup filter, settings check; markers in `RequestLogMiddleware.cs`, `RequestOutcome.cs`, `RequestLogSink.cs`), registration in `ZhPvpModules.cs`, `ZhPvp:RequestLog:Enabled` in `appsettings.Local.json`, `tests/ZhPvp.Tests/Host/RequestLogTests.cs` | The whole folder, its registration, setting and tests (framework request event + console sink in the local flavor file) | Any game-specific per-request behaviour, rebuilt on the outcome feature (FR-084) |

Markers that stay open after this slice: FG-005, FG-008, FG-009 (deferred packaging) and those owned by slices 014–016.

## Dependencies and seams with later slices

- **014 (FG-026 B, FG-028, FG-021, FG-027)**: FG-027's mapping check will be shipped in the consumer test kit of FR-010, so
  the kit must accept additions without breaking consumers; the route catalog may give request events a stable route name
  (FR-081 uses the route template until then); the consumer guide leaves a placeholder for game proto codegen.
- **015 (FG-018, FG-023, FG-024, FG-019)**: the `IGameConfig` test double of FR-021 should be reusable by config-validation
  tests; config not-modified in the request outcome (FR-084) must survive the version-hash changes of FG-024.
- **016 (FG-013, FG-015, FG-029, FG-016, FG-022)**: the command-context double takes command limits from the same source
  as the runner (FR-023, FR-025), so limit changes of FG-016 reach both; the request event logs the request id, which
  FG-022 may let the game supply.
- **Deferred FG-005/FG-008/FG-009**: the consumer test kit depends on the stores where they are (FR-014); build sealing
  (FR-030..FR-033) is the part of consumer build hygiene that does not need packaging; `run-local` host parameters do not
  depend on a reusable composition host.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the flavor × composition matrix (local, dev, beta, uat, live × with/without the cheat module; plus
  service-only and no-player-API variants), 100% of non-development combinations with a Dev endpoint or command fail at
  startup and 100% of development combinations start; with the validator bypassed, 100% of Dev requests in a Staging host
  answer 403 with 0 state changes, 0 session touches and 0 analytics events, and 100% of direct runner calls for Dev
  commands are refused with 0 documents written.
- **SC-002**: A consumer-shaped test project that references only the consumer test kit (no `tests/Shared`, no sample
  reference) builds with 0 warnings and passes a handler unit test and a local-flavor smoke test; after the run, 0 files
  exist outside its temporary folders.
- **SC-003**: The conformance test runs every access rule of FR-023 (at least 7 cases) through the double and the runner's
  context with identical outcomes (100%); `NewId`/`NewSeed` match the runner's for 100% of sampled receipt ids and
  purposes.
- **SC-004**: Building every framework project in Release from a checkout under a parent folder named `tests` with a
  hostile parent `Directory.Build.targets` gives 0 errors and 0 warnings, `IsTestProject` true for exactly the projects
  under the framework's `tests/`, and banned-API analyzers active on 100% of the others.
- **SC-005**: `run-local.ps1` and `run-local.sh` start a second (fixture) host to ready using only the new parameters; the
  default invocation reproduces every slice 011 contract behaviour (ready, Ctrl+C stop, exit codes 2–7) in the
  repeatable check of FR-041; the first consumer can delete both of its ~200-line copies.
- **SC-006**: Framework version shown by assemblies, Unity package, startup log, telemetry resource and GM ops read is
  identical in 100% of checks; a deliberate mismatch fails the version test.
- **SC-007**: In the scripted request session of story 3, 100% of non-health requests produce exactly one request event
  with all FR-081 fields and the code the client received; 0 events for health probes; a secret scan over all events
  finds 0 tokens, secrets, cookies or bodies; a consumer middleware reads the same code for 100% of requests.
- **SC-008**: With request logging and the console sink on, p95 request latency rises by at most 1 ms versus off on the
  reference development machine (in-process comparison); with both sinks blocked, 100% of requests still succeed, memory
  stays bounded and drops are counted.
- **SC-009**: The consumer-guide walkthrough of story 7 reaches a green handler test, a green local-flavor smoke test and a
  running host without reading framework source; every gap found is fixed before the slice closes (recorded in tasks.md).
- **SC-010**: After the game bumps to `v0.7.0`, the markers of FG-001, FG-002, FG-003, FG-004, FG-006, FG-007, FG-010 and
  FG-025 can all be removed (8 of the 20 marker ids open on 2026-11-03), and the `UPGRADE.md` sync lines for
  `tests/Shared`, the smoke test and `tools/local` are no longer needed.

## Assumptions

- "Development tools allowed" stays defined as deployment environment Development or Test (`AllowsDevelopmentTools`); no
  new switch is introduced, and the deferred Staging cheat switch stays deferred (PCR platform §17, R13). This is stricter
  than OPS-09 ("environment ≠ Production"), in line with the rule slice 011 already applied to test authentication, the
  GM staff seed and fake verifiers for beta/uat.
- The refused Dev request answers 403 `FORBIDDEN` (the existing deny-by-default code) rather than 404, so the response is
  the same as for an endpoint without profile; the endpoint should not exist at all outside development anyway.
- The consumer test kit is delivered as framework project(s) referenced by path from the submodule; NuGet packaging stays
  with FG-005/FG-008/FG-009. Exact project names, namespaces and which helpers move from `tests/Shared` are plan
  decisions.
- The consumer test kit does not depend on a unit-test framework; the framework's own tests keep xUnit v3 and Shouldly.
- The command-context double does not simulate transactions, retries, the receipt or the outbox relay; those stay covered
  by in-process host tests.
- The game error band 50000–89999 is the friction log's proposal and contains the band the first consumer already uses
  (50000–50999); 90000–99999 stays framework territory (template 90000–90099).
- The version declared in this slice is `0.7.0` (the tag that closes the round); builds between this slice and the tag are
  told apart by the source revision in the informational version. The Unity package version is raised to the same value.
- Versions are not shown on public endpoints (health probes are public, OBS-03); the GM ops read with the existing
  `ops.read` permission is the operator-facing channel, plus logs and telemetry.
- Player and staff ids in request events are internal ids, logged like in other operational events under the existing
  LOG-04/TBD-08 policy; no new PII category is added.
- The console sink format is human-readable one line (not compact JSON); structured consumers read the files.
- `run-local.sh` keeps the slice 011 limitations (bash ≥ 4.4 for the kill fallback; not run on macOS/Linux unless a runner
  is available).
- `start-hosts.ps1`/`stop-hosts.ps1` (two sample instances) stay sample-specific; only `run-local` gains host parameters.

## Out of Scope

- Slice 014: route catalog shared by server and client (FG-026 direction B), snapshot section ↔ proto type contract
  (FG-028), Unity codegen for the game's own `.proto` (FG-021), aggregate state ↔ proto ↔ wire mapper check (FG-027,
  which will use this slice's consumer test kit).
- Slice 015: config validation per file and eager catalogs (FG-018), startup checks that need config (FG-023),
  `config_version` covering `clientVisible` (FG-024), client config distribution (FG-019).
- Slice 016: login snapshot and new-player flag (FG-013), account-creation hook (FG-015), one broken aggregate failing
  snapshot/login (FG-029), command limits and result/receipt size (FG-016), client-supplied request ids (FG-022).
- Deferred: File/InMemory stores out of `GameServer.Platform.Testing` (FG-005), build settings flowing to consumers
  (FG-008), reusable composition host (FG-009) — packaging/NuGet; `MapPlayerQuery` helper (FG-014), per-resource
  visibility/floor (FG-017), lazy schema upgrade write-back and metrics (FG-030), Local vs Couchbase cross-collection
  consistency in tests (FG-031). FG-020 (hot reload) is game-only.
- Per-request logging inside the Unity SDK, the Unity Editor server window itself, log shipping, dashboards.
- Any change to the player API wire contract, persisted documents, or GM frontend (another developer, owner 2026-10-27).

## Friction claims re-checked on `main` (`fea3d29`)

- FG-010: `EndpointSecurityValidator.cs:86` checks only `options.IsProduction`; `ProfileEnforcementMiddleware.cs:71-77`
  authenticates any non-Player/non-Gm profile (Dev and any unknown value) through the default branch (player schemes, then
  GM schemes); `CommandRunner.cs:154` `SecurityProfile.Dev => true`. All hold.
- FG-001: holds. Precision: `tests/Shared/GmBrowser.cs` already has `TestServer` overloads (lines 19, 41) but also
  extends `TestHost`, so it cannot be linked alone; `SampleHostSmokeTests` sets 6 of the 7 settings (no
  `Observability:Logging:Directory`).
- FG-002, FG-003, FG-004, FG-006, FG-007, FG-011: hold as written.
- FG-012: holds; precision: the `../../../src` path is in `templates/module/README.md`, not in `02-them-command.html`; the
  commit stamp `175df65` is on all four HTML pages.
- FG-025: holds (`CommandRunner.cs:37-55, 66-69`; `ObservabilityExtensions.cs:40-87`); precision: `ExceptionMiddleware`
  also logs Information for bad HTTP requests and invalid Protobuf and Error for 5xx, but never a line for successful
  requests or business rejections at HTTP 200.
