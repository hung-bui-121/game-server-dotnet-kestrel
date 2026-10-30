# Feature Specification: Environments per flavor and easy local run — Couchbase for dev/beta/uat/live, file-based store for local (slice 011)

**Feature Branch**: `011-local-env`

**Created**: 2026-10-30

**Status**: Draft

**Input**: Owner tasks 1 and 2 of `tasks/task_30102026.txt` (2026-10-30). Task 1: "set up the service environments:
dev/beta/uat/live use Couchbase; local uses a file that emulates Couchbase; the settings needed to configure each flavor
(the attached `database_settings.json` is only a reference, a newer Couchbase may need more specific parameters)" —
reference: local `{dbcache|dbbase: {localFolder}}`, Couchbase `{dbcache: memcached bucket, dbbase: couchbase bucket;
bucketName, bucketType, serverIp, operationTimeOut 2000, compressThreshold 2048, moxiPort}`. Task 2: "a way to run the
server as type local (listening on localhost and a specific port) so a developer can easily start the service locally".
Owner decisions 2026-10-30 (`AgentMemories/features/task-30102026/00_plan.md`): drop `dbcache` (one main bucket only);
flavor map local/dev → Development, beta/uat → Staging, live → Production (beta/uat have no real payments, IAP sandbox);
packaging (task 4) out of scope; plan defaults for Q4 (game config overlay per deployment environment), Q5 (one JSON file
per document, no hot reload, only the framework's queries), Q6 (Production provisioning command in this slice), Q7
(`-Lan` allowed for local only, with a warning).

## Scope note

- Already in place (P0–010): `GameServer:DeploymentEnvironment` (Development / Staging / Production / Test, OPS-01)
  that gates development tools (test authentication, cheat module, GM staff seed, GM OpenAPI, fake IAP verifiers and
  identity providers); `GameServer:EnvironmentId` and `GameId` required at startup (ARC-10); store kinds `InMemory` and
  `Couchbase`, the in-memory store refused outside Development/Test; Couchbase settings `ConnectionString`, `Username`,
  `Password`, `BucketName`, `UseGetMulti`, `ProvisionOnStartup` (default on only in Development); provisioner that
  creates scope `g_<gameId>`, the declared collections, `txn_meta` and the indexes (DAT-07), today only at startup;
  secret keys refused in configuration files outside Development/Test (OPS-08); shared store conformance suite
  (in-memory and Couchbase), including the 010 due and reference queries; `tools/local/start-hosts.ps1` running N hosts
  on a local Couchbase at ports 5080+; health routes `/health/live`, `/health/ready`; GM UI `/gm/v1/ui`.
- Gaps this slice closes: no flavor concept or per-flavor configuration files (only one `appsettings.json` that carries
  development-only values); no local store that keeps data across restarts; no Couchbase timeout, TLS or compression
  settings; no provisioning path for Production other than turning startup provisioning on; no launch profile, fixed
  local port or one-command local run; the host environment name is used as deployment environment without checking
  that it is a known value.
- Not included: audit logs (task 3, separate slice), library packaging (task 4, skipped by owner), HTML documentation
  (task 5), shared cache bucket, CI pipeline (CI-01), container/Linux deployment guide and runbooks (slice 013), GM
  frontend (another developer, owner 2026-10-27).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Developer starts the server locally with one command (Priority: P1)

A game developer clones the repository on a machine that has only the .NET SDK (no Couchbase, no Docker). With one
command they get a running server on `http://localhost:5080`, see which URLs to use (player API, GM UI, GM OpenAPI,
health), point the Unity sample client at it and play. Stopping and starting again keeps the players and progress they
created.

**Why this priority**: This is owner task 2 and the daily loop of every game team using the framework; today the default
run loses all data on stop and uses a port nobody configured.

**Independent Test**: On a clean checkout run the local script; within 60 s of the build finishing the readiness route
answers 200 on port 5080, the script prints the URLs, a guest login and a command succeed; stop with Ctrl+C, start again,
log in with the same guest credential and see the same player and progress.

**Acceptance Scenarios**:

1. **Given** a clean checkout and the .NET SDK, **When** the developer runs the local script (PowerShell on Windows or
   the shell script on macOS/Linux) without arguments, **Then** the host is built, started in the local flavor,
   listens on the loopback address only at port 5080, and the script returns control (logs streaming) once readiness
   answers 200, printing the player API base URL, health URL, GM UI URL and GM OpenAPI URL (Task 2).
2. **Given** an IDE or `dotnet run` on the sample host without arguments, **When** the developer starts it, **Then**
   the default launch profile is the local flavor on the same port with the same data folder as the script.
3. **Given** port 5080 is already in use, **When** the script starts, **Then** it stops with a message naming the port
   and how to choose another one, exit code non-zero, and no half-started host is left running.
4. **Given** the developer passes another port, **When** the host starts, **Then** it listens on that port only, and the
   printed URLs use it.
5. **Given** a running local host, **When** the developer presses Ctrl+C, **Then** the host stops gracefully (readiness
   503, in-flight commands finish, logs flushed) and all committed data is on disk.
6. **Given** the developer asks for a reset, **When** the script runs, **Then** the local data folder (and nothing else)
   is deleted before start and the server starts empty.
7. **Given** the developer asks for LAN access to test on a phone, **When** the script runs, **Then** the host listens
   on all interfaces and a prominent warning says that the GM UI with the development staff seed, test authentication
   and cheats are reachable from the network; LAN binding is refused for any flavor other than local (Q7).
8. **Given** the developer wants the shared team Couchbase instead of local files, **When** they choose the dev
   launch profile, **Then** the host runs the dev flavor with the Couchbase password taken from a user environment
   variable, never from a file.
9. **Given** the multi-instance script `tools/local/start-hosts.ps1`, **When** it is run as before, **Then** it still
   starts N hosts on Couchbase at ports 5080, 5081… (it selects the dev flavor explicitly).

---

### User Story 2 - Local data lives in files and behaves like Couchbase (Priority: P1)

The local flavor stores data in a folder of readable JSON files, one per document, instead of memory. Everything the
framework does on Couchbase — command transactions with receipts, sessions with expiry, outbox, GM queries, lease jobs
and the 010 account purge — works the same way locally, and a crash never leaves a half-applied command.

**Why this priority**: Owner task 1 asks for "a file that emulates Couchbase" for local; without persistence the local
run of story 1 has no value beyond unit tests, and if it behaves differently from Couchbase, bugs found on dev/beta are
not reproducible locally.

**Independent Test**: Run the shared store conformance suite and the 010 purge tests against the file store; then run
a fault-injection test that kills the process at every step of a multi-document commit and checks after restart that
the commit is either fully present or fully absent.

**Acceptance Scenarios**:

1. **Given** the local flavor, **When** a command commits (aggregates, receipt, claim markers in one transaction),
   **Then** before the response is returned every written document is persisted in the data folder; after a restart
   the same reads return the same content (Task 1).
2. **Given** a multi-document commit, **When** the process is killed at any moment during the commit, **Then** after
   restart either all documents of that commit are present or none is; no partially written file is ever read as a
   document (DAT-04 semantics).
3. **Given** the file store, **When** the shared store conformance suite (transactions and conflicts, KV insert /
   replace with CAS / remove, expiry, transaction-only collections, unknown collections, paged queries, pending outbox,
   due and reference queries) runs, **Then** every case passes with the same results as the in-memory and Couchbase
   stores.
4. **Given** a session or any document written with an expiry, **When** the expiry passes (including while the host was
   stopped), **Then** the document is not returned after restart, as on Couchbase.
5. **Given** a running local host on a data folder, **When** a second host is started on the same folder, **Then** the
   second host fails at startup with a message naming the folder and saying another host is using it; the first host
   and the data are unaffected. **Given** the first host crashed, **When** a new host starts, **Then** it is not blocked.
6. **Given** a stopped host, **When** the developer opens the data folder, **Then** documents are grouped by game scope
   and collection, one JSON file per document whose name shows the document key, and a hand edit made while the host
   is stopped is visible after the next start (Q5).
7. **Given** a document file that is not valid JSON or not readable, **When** the host starts, **Then** startup fails
   naming the file; nothing is skipped silently.
8. **Given** `GameServer:DeploymentEnvironment` is Staging or Production, **When** the file store is configured,
   **Then** startup fails with a message saying the file store is for Development/Test only and how to switch to
   Couchbase.
9. **Given** a GM staff signed in to the local GM UI, **When** the host restarts, **Then** the GM session and the data
   protection key ring survive (they are stored like any other document), so the staff does not have to sign in again
   until the session expires.

---

### User Story 3 - Operations deploys a flavor by naming it (Priority: P1)

DevOps deploys the same build to dev, beta, uat and live. Each deployment selects its flavor with one setting; the
flavor's configuration file supplies non-secret settings, environment variables supply secrets. A wrong or missing
flavor never results in a server that runs with development settings; every mistake is reported at startup before
traffic, with the key to fix.

**Why this priority**: Owner task 1 ("environment per flavor"); a live server that silently inherits development
settings (test authentication, cheats, GM seed accounts) would be a security incident (OPS-01, OPS-09, SEC-03, SEC-16).

**Independent Test**: Start the sample host once per flavor with that flavor's file plus the required environment
variables and see each one become ready (Couchbase flavors against a test bucket); then run the misconfiguration matrix
(missing deployment environment, unknown value, `live` mapped to Development, file store in Staging, secret in a
committed file, missing Couchbase settings) and see each one fail at startup with a message naming the key.

**Acceptance Scenarios**:

1. **Given** the host environment name set to a flavor (`Local`, `Dev`, `Beta`, `Uat`, `Live`), **When** the host
   starts, **Then** the shared base configuration and that flavor's file are loaded, environment variables and
   command-line arguments override both, and the effective `EnvironmentId` / `DeploymentEnvironment` / store are those
   of the flavor table (FR-002).
2. **Given** a flavor whose file does not set `GameServer:DeploymentEnvironment` and whose host environment name is not
   itself a valid deployment environment (e.g. `Local`), **When** the host starts, **Then** startup fails naming the key,
   the allowed values and the flavor table; today such a host silently runs as a non-development, non-production
   environment.
3. **Given** `GameServer:DeploymentEnvironment` set to a value outside Development / Staging / Production / Test,
   **When** the host starts, **Then** startup fails naming the key and the allowed values.
4. **Given** `EnvironmentId` is a standard flavor name and `DeploymentEnvironment` does not match the flavor table
   (e.g. `live` with Development, `beta` with Production), **When** the host starts, **Then** startup fails naming both
   keys.
5. **Given** a deployment that forgot to select a flavor, **When** the host starts, **Then** it does not run with
   development settings: the base configuration contains no deployment environment, environment id, store kind,
   development staff seed or secret, so startup fails on the missing required keys.
6. **Given** the beta or uat flavor (Staging), **When** the host starts, **Then** development tools are off (no test
   authentication, cheats, GM staff seed, GM OpenAPI, fake verifiers or fake identity providers), store receipts from
   the Apple sandbox and Google test purchases are accepted, and the game config overlay of Staging is used (beta and
   uat share it, Q4).
7. **Given** the live flavor (Production), **When** the host starts without a required secret (Couchbase password,
   data protection master key, GM SSO client secret, IAP credentials), **Then** startup fails listing every missing key
   at once with the environment variable name to set.
8. **Given** any committed flavor file of the sample host, **When** the repository tests run, **Then** no secret key
   listed in `GameServer:SecretKeys` has a value in it — including the local and dev files, where the runtime secret
   check does not apply (OPS-08, ACC-48).

---

### User Story 4 - Couchbase connection tuned for the current SDK (Priority: P2)

DevOps configures each Couchbase flavor with the settings the current Couchbase SDK uses — connection string with one
or more nodes, TLS with a trusted CA, operation and query timeouts, compression, connection pool — and knows exactly how
each field of the team's old `database_settings.json` maps or why it is gone.

**Why this priority**: Needed for beta/uat/live readiness and asked by the owner, but the dev flavor already works with
today's settings, so local work (stories 1–3) is not blocked by it.

**Independent Test**: Start a Couchbase flavor with each new setting changed from its default and observe the effect
(timeouts honoured by a delayed operation, TLS connection with a self-signed CA); start with each invalid value and see
startup fail naming the key; read the mapping table in the guide.

**Acceptance Scenarios**:

1. **Given** a connection string listing several nodes, **When** the host starts, **Then** it connects through any
   reachable node (seed list), as with the old `serverIp`.
2. **Given** a TLS connection string and a trusted CA certificate file, **When** the host starts, **Then** the
   connection is encrypted and the certificate is validated against that CA; **Given** the file is missing or not a
   certificate, **Then** startup fails naming the key.
3. **Given** the Production flavor with a non-TLS connection string, **When** the host starts, **Then** startup fails
   (SEC-01: database traffic uses TLS); in Staging the host starts and logs a warning; in Development nothing is
   reported.
4. **Given** a KV operation timeout and a query timeout, **When** an operation exceeds them, **Then** it fails with the
   store-unavailable / timeout error the framework already maps, within the configured time.
5. **Given** an out-of-range value (non-positive timeout, timeout longer than the request timeout of the endpoint group
   that uses it, negative compression size, invalid connection-string scheme), **When** the host starts, **Then**
   startup fails naming the key and the allowed range.
6. **Given** the owner's reference keys (`moxiPort`, `bucketType`, `dbcache`) left in a configuration, **When** the host
   starts, **Then** it logs a warning that the key is not used and why, and starts.

---

### User Story 5 - Production schema is provisioned by an ops command (Priority: P2)

Before a release, the deploy pipeline runs one command that creates the game scope, every collection the configured
modules need (including `txn_meta`) and every index, with an administrative credential, and exits. The API instances
then run with a credential that has data rights only and never create schema themselves.

**Why this priority**: DAT-07 requires "startup in Dev, ops command in Production"; today Production can only provision
by giving every instance management rights at startup (against least privilege, SEC-01). Owner Q6: do it in this slice.

**Independent Test**: Against an empty scope run the provisioning command: exit 0 and everything created; run it again:
exit 0, nothing created; start a Production-flavor host with startup provisioning off on that scope: it becomes ready;
start one on an unprovisioned scope: it does not become ready and names the missing collections and the command.

**Acceptance Scenarios**:

1. **Given** the host build and the flavor's configuration, **When** the provisioning command runs, **Then** it creates
   the scope `g_<gameId>`, every collection required by the registered modules and the platform (including `txn_meta`
   first, before anything can run a transaction), and every index the framework and modules declare, then exits 0
   without serving traffic or running background jobs (DAT-07).
2. **Given** everything already exists, **When** the command runs again, **Then** it changes nothing, reports each item
   as already present, and exits 0 (idempotent).
3. **Given** a failure (bucket missing, insufficient rights, cluster unreachable, index build error), **When** the
   command runs, **Then** it exits non-zero with a message naming the item and the cause; a re-run after the fix
   completes the remaining items.
4. **Given** the command runs in a pipeline that has only the deployment identity and the Couchbase settings and
   credential, **When** it runs, **Then** settings unrelated to the database (GM SSO secret, IAP credentials, data
   protection master key) are not required.
5. **Given** a Staging or Production host with startup provisioning off and a scope missing a required collection,
   **When** the host starts, **Then** it does not serve traffic, and the message lists the missing items and names the
   provisioning command.
6. **Given** a Development host (local excluded), **When** it starts, **Then** startup provisioning stays on by default,
   as today.

---

### User Story 6 - A new developer finds one documented way (Priority: P3)

A developer new to the project reads the README and the local guide and finds one way to run locally, the flavor table,
the settings each flavor needs, the Couchbase settings with the old-to-new mapping, how to reset local data and how to
provision Production.

**Why this priority**: Makes stories 1–5 usable without asking the framework team; the docs are also input for the
later deploy document (task 5c).

**Independent Test**: A developer who has not used the repository follows only the README and guide and reaches a ready
local server and a ready dev-flavor host without help; every configuration key the docs mention exists in the options
classes.

**Acceptance Scenarios**:

1. **Given** the README, **When** a developer looks for how to run locally, **Then** there is exactly one primary path
   (the local script / launch profile), with the Couchbase multi-instance path presented as the "real environment"
   option.
2. **Given** the guide and the platform PCR, **When** ops reads them, **Then** they find the flavor table, the required
   settings and environment variable names per flavor, every Couchbase setting with default and allowed range, the
   reference mapping with dropped fields, the file store semantics and limits, and the provisioning command contract
   (exit codes, idempotency, credential).

---

### Edge Cases

- Host environment name `Development` (used by IDE defaults and test factories) with no flavor file: accepted as a
  valid deployment environment, but `EnvironmentId` and the store kind must come from somewhere (test settings or a
  flavor); the sample host smoke test supplies its own settings.
- No host environment name at all: the host framework treats it as Production; with the cleaned base file this fails
  closed on required keys instead of running with development settings.
- A configuration array in the base file (e.g. a GM staff seed) cannot be removed by a flavor file because arrays merge
  by index; therefore development-only arrays live only in the local/dev flavor files, never in the base file.
- Port set in more than one place (launch profile URL, the flavor file's URL setting, `--urls`): the command line wins,
  then environment, then file; the launch profile and the local flavor file use the same port so the developer never
  sees two different defaults.
- Listening on `localhost` resolves to both IPv4 and IPv6 loopback; a client using `127.0.0.1` or `::1` both work.
- Development deployment bound to a non-loopback address outside the LAN option (e.g. `--urls http://0.0.0.0:5080`):
  the host logs a warning at startup (development tools reachable from the network).
- Two developers' local folders accidentally on a network share: the single-host lock still holds; performance is not
  a goal there.
- Document keys with characters invalid in file names (`::`, `/`, `\`, `:`), keys differing only by letter case on
  case-insensitive file systems (Windows, default macOS), and very long keys (path length limits): the key-to-file
  mapping stays reversible, collision-free and within limits.
- Disk full or permission denied while persisting a commit: the commit fails with the store-unavailable error and is
  not acknowledged; nothing partial is visible after restart; readiness reports the store as unhealthy.
- Clock: expiry is evaluated with the server clock (`IClock`); a development clock offset applies to the local store the
  same way as to Couchbase expiry semantics.
- Hand edit while the host is running: may be overwritten by the next commit of that document; documented, no hot
  reload (Q5).
- Local folder written by an older version of the store format: the store detects an unknown format version and fails
  startup with "reset the local data" guidance rather than guessing.
- Reset must never delete anything outside the configured data folder (a misconfigured folder such as the repository
  root or a drive root is refused).
- A module that relies on a Couchbase-only query feature (its own N1QL) does not work on the file store; only the
  framework's store contracts are emulated (Q5).
- Unit tests keep using the in-memory store (no disk); the file store does not replace the Couchbase integration tests
  required by TST-01 / constitution VI.
- Provisioning while API instances of an older version are running: creating collections and indexes is additive and
  safe; dropping collections is never done by the command (never delete `txn_meta`, DAT-07).
- Index creation on a large existing collection can take minutes: the command waits for completion within its own
  timeout and reports progress; a timeout exits non-zero and a re-run continues.
- Beta and uat need different game config (e.g. shop prices): not supported by this slice (overlay per deployment
  environment, Q4); would need a per-environment-id overlay layer (CFG-01 change).

## Requirements *(mandatory)*

### Functional Requirements

**Flavors and startup validation (Task 1; OPS-01, OPS-08, ARC-10, DAT-01, CFG-01)**

- **FR-001** (Task 1, OPS-01): Five standard flavors `local`, `dev`, `beta`, `uat`, `live`. A deployment selects its
  flavor by the host environment name; this loads the flavor's configuration file over the shared base file;
  environment variables and command-line arguments override file values. Games may add more flavors.
- **FR-002** (Task 1, OPS-01, DAT-01, owner Q2/Q3): Flavor table:

  | Flavor | Deployment environment | `EnvironmentId` | Store | Data |
  | --- | --- | --- | --- | --- |
  | local | Development | `local` | file store | local data folder |
  | dev | Development | `dev` | Couchbase | team bucket (single node: replicas 0, OPS-01) |
  | beta | Staging | `beta` | Couchbase | own bucket; IAP sandbox, no real payments |
  | uat | Staging | `uat` | Couchbase | own bucket; IAP sandbox, no real payments |
  | live | Production | `live` | Couchbase | separate cluster (DAT-01) |

- **FR-003** (OPS-01, ARC-10): `GameServer:DeploymentEnvironment` MUST be one of Development, Staging, Production, Test
  (case-insensitive) and every flavor file sets it explicitly. When the key is absent, the host environment name is
  used only if it is itself one of these values; any other effective value fails startup naming the key, the allowed
  values and the flavor table.
- **FR-004** (OPS-01, OPS-09): When `EnvironmentId` is one of the five standard flavor names, a deployment environment
  different from FR-002 fails startup naming both keys. Other environment ids are allowed with an explicit deployment
  environment.
- **FR-005** (OPS-08, SEC-03, SEC-16): The shared base configuration contains only values valid in every flavor: no
  deployment environment, environment id, store kind, development staff seed or secret. A deployment with a missing or
  mistyped flavor fails closed on required keys and never runs with development settings.
- **FR-006** (OPS-08, SEC-11, ACC-48): No committed flavor file holds a value for any key in `GameServer:SecretKeys`,
  in any flavor including local and dev (where the runtime secret check is skipped); an automated repository test
  enforces this for the sample host. Secrets come from environment variables (or a secret provider).
- **FR-007** (Task 1): Store kind is a setting with three values: in-memory (tests and throwaway runs), file store
  (local), Couchbase. In-memory and file store are refused at startup outside Development/Test, with a message naming
  the store and the fix.
- **FR-008** (CFG-01, owner Q4): The game config overlay stays per deployment environment (`config/env/<Development|
  Staging|Production>`); beta and uat share the Staging overlay. No per-flavor overlay in this slice.
- **FR-009** (OPS-01, ARC-06, ACC-26): Each flavor's required settings are documented with their environment variable
  names; starting a flavor without them fails listing every missing key at once. Staging and Production require at
  least the Couchbase connection, bucket, username and password, the data protection master key, GM SSO, real IAP
  verifiers and real identity providers (existing rules); local requires none.
- **FR-010** (OPS-09, SEC-03): Development tools follow the deployment environment as today: on for local and dev, off
  for beta, uat and live. Apple sandbox / Google test receipts are accepted outside Production only (existing rule).

**Couchbase settings (Task 1; SEC-01, DAT-01, DAT-05, PERF-03)**

- **FR-011** (Task 1): Couchbase settings: connection string (one or more nodes; TLS scheme for encrypted traffic),
  username, password (secret), bucket (one per environment), KV operation timeout, query timeout, trusted CA certificate
  file (for self-signed clusters), compression on/off and minimum document size to compress, KV connections per node.
  Scope is always `g_<gameId>` and is not configurable. Defaults are the SDK defaults, recorded in PCR.
- **FR-012** (Task 1, owner Q1): Mapping of the owner's reference is documented: `dbbase.bucketName` → bucket;
  `serverIp` → connection string; `operationTimeOut` → KV operation timeout; `compressThreshold` → compression minimum
  size; `bucketType: couchbase` → not an application setting (transactions require a Couchbase bucket; storage backend
  is chosen when ops creates the bucket); `localFolder` → file store folder. Dropped: `dbcache` (no consumer — the
  framework keeps caches per instance and sessions in a collection with expiry; owner Q1), `bucketType: memcached`
  (memcached buckets are deprecated and cannot run transactions), `moxiPort` (Moxi proxy no longer exists; the SDK
  bootstraps from the connection string on the standard ports).
- **FR-013** (ARC-06): Couchbase settings are validated at startup before traffic: missing connection string, bucket or
  username; missing password outside Development; non-positive timeouts or timeouts longer than the request timeout of
  the endpoint group using them; unreadable CA file; negative compression size; unsupported connection-string scheme.
  Each failure names the key and the allowed range.
- **FR-014** (SEC-01): In Production the database connection MUST use TLS; a non-TLS connection string fails startup.
  In Staging it logs a warning; in Development nothing is reported.
- **FR-015**: Unused keys from the old reference (`moxiPort`, `bucketType`, `dbcache` section) found in configuration
  produce a startup warning naming the key and why it is ignored; they do not block startup.
- **FR-016** (PERF-03, DAT-07): One cluster connection per process (unchanged); the settings apply to every user of it
  (commands, jobs, outbox sweeper, provisioning), and the transaction metadata collection setting stays identical on
  every process.

**Local file store (Task 1; owner Q5; DAT-03, DAT-04)**

- **FR-017** (Task 1): The file store keeps all data in one data folder (setting; relative paths resolve against the
  content root; default for the sample host follows the owner's reference `../_database/dbbase`); the folder is
  created when missing and is ignored by git.
- **FR-018** (Task 1): The file store implements every framework store contract with the same observable behaviour as
  the in-memory store: optimistic transactions with conflict retry and all-or-nothing commit, KV insert / CAS replace /
  remove, expiry, transaction-only collections, refusal of undeclared collections, paged queries, pending outbox
  queries, due and reference queries (010). It passes the shared store conformance suite and the 010 purge tests.
- **FR-019** (DAT-04): A write is persisted before the operation that commits it reports success.
- **FR-020** (DAT-04): After a crash at any moment, a multi-document commit is visible either completely or not at
  all; incomplete files are never read as documents.
- **FR-021** (owner Q5): One JSON file per document, grouped by game scope and collection, holding the stored content
  and its expiry; the key-to-file-name mapping is reversible, collision-free on case-insensitive file systems and within
  path length limits. The folder records a store format version; an unknown version fails startup with reset guidance.
- **FR-022** (DAT-03): Expiry is stored as an absolute UTC time; expired documents are never returned, including after
  a restart, and are removed from disk eventually.
- **FR-023**: Only one host may use a data folder at a time; a second host fails at startup naming the folder; the
  lock is released when the holder exits, including by crash.
- **FR-024** (ARC-06): An unreadable or malformed document file fails startup naming the file. Hand edits are supported
  only while the host is stopped; no hot reload.
- **FR-025**: Deleting the data folder resets the local store; the next start begins empty.
- **FR-026** (owner Q5): Only the framework's store contracts are emulated; no arbitrary N1QL. The limit is stated in
  PCR and the guide.
- **FR-027** (OPS-01): The file store is a development-only store: refused outside Development/Test (FR-007).
- **FR-028** (OPS-04): Everything the framework stores in the document store (player data, receipts, sessions, GM
  sessions, data protection key ring, leases, purge records) persists in the file store, so player and GM sessions
  survive a local restart until they expire.

**Production provisioning (DAT-07, SEC-01, owner Q6)**

- **FR-029** (DAT-07): The host offers a provisioning mode (a command-line switch of the host binary) that creates the
  scope, every required collection with `txn_meta` before any other, and every framework and module index for the
  registered modules, then exits without serving traffic or running jobs. Exit 0 on success; non-zero with a message
  naming the item and the cause on failure.
- **FR-030** (DAT-07): Provisioning is idempotent: a re-run creates nothing, reports items as already present and exits
  0; a re-run after a partial failure completes the rest. It never drops or modifies existing collections or indexes.
- **FR-031** (DAT-07, SEC-01): Provisioning may run with its own administrative credential supplied at run time, so the
  API instances need data rights only. It requires only the deployment identity (game id, environment id, deployment
  environment) and the Couchbase settings; settings unrelated to the database are not required in this mode. It works
  in every deployment environment, including Production. It does not create buckets.
- **FR-032** (DAT-07): Startup provisioning stays on by default in Development and off elsewhere. A host with startup
  provisioning off whose scope is missing a required collection does not serve traffic; the message lists the missing
  items and names the provisioning command. The check uses only the rights of the runtime credential.

**Local run (Task 2; OPS-02)**

- **FR-033** (Task 2): The local flavor listens on the loopback address only, port 5080 by default, set by one
  setting; command-line and environment overrides still work, so `tools/local/start-hosts.ps1` keeps assigning ports
  5080+ (and selects the dev flavor explicitly).
- **FR-034** (Task 2): The sample host has launch profiles `local` (default, local flavor, opens the GM UI) and
  `dev-couchbase` (dev flavor, Couchbase password from a user environment variable).
- **FR-035** (Task 2, OPS-02): One-command local scripts for PowerShell and POSIX shell build the host, start the local
  flavor, wait for readiness (at most 60 s after build), print the player API base, health, GM UI and GM OpenAPI URLs,
  keep streaming logs, and stop the host gracefully on Ctrl+C.
- **FR-036** (Task 2, owner Q7): Script options: port; reset (deletes only the configured local data folder before
  start, refusing a folder that is not a local data folder); LAN (listen on all interfaces with a prominent warning;
  local flavor only).
- **FR-037** (Task 2): Script failures exit non-zero with a clear message and leave no host running: port in use
  (names the port and the option to change it), .NET SDK not found, readiness timeout (prints the last log lines),
  data folder locked by another host.
- **FR-038** (SEC-03, OPS-09): A Development deployment listening on a non-loopback address logs a startup warning that
  development tools are reachable from the network.
- **FR-039** (Task 2): The Unity sample client and the SDK smoke tooling default to `http://localhost:5080/`.

**Documentation and contract (OPS-02, PCR-01)**

- **FR-040** (OPS-02): README has one primary local-run path; the local guide covers the flavor table and selection,
  required settings and environment variable names per flavor, Couchbase settings with the reference mapping and
  dropped fields, file store layout / reset / limits, the provisioning command, the local scripts and launch profiles.
- **FR-041** (PCR-01): The platform PCR records: store kinds and where each is allowed; file store semantics
  (durability, crash atomicity, lock, expiry, supported queries, limits); Couchbase settings with defaults and bounds;
  flavor validation rules; provisioning command contract (mode, exit codes, idempotency, credential); default local
  port.

### Key Entities

- **Flavor**: named deployment profile (`local`, `dev`, `beta`, `uat`, `live`, or game-defined) — selected by the host
  environment name; owns one configuration file; maps to one deployment environment, environment id and store kind.
- **Deployment environment**: Development / Staging / Production / Test; decides development tools, secret-source
  rules, provisioning default and the game config overlay.
- **Flavor configuration file**: non-secret settings of one flavor layered over the shared base file.
- **Store kind**: in-memory / file store / Couchbase, with the deployment environments where each is allowed.
- **Local data folder**: per-host folder of the file store — document files grouped by scope and collection, commit
  journal, format version, single-host lock.
- **Couchbase connection settings**: connection string, credential, bucket, timeouts, TLS trust, compression,
  connection pool.
- **Provisioning run**: one execution of the provisioning mode — items created / already present / failed, exit code.
- **Local run script and launch profiles**: the developer entry points with port, reset and LAN options.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a machine with only the .NET SDK, a developer goes from a fresh clone to a ready local server with one
  command; readiness is reached within 60 s after the build completes.
- **SC-002**: 100% of data committed before a normal stop or a forced kill is present after restart; across a
  fault-injection run that kills the host at every step of a multi-document commit, 0 partially applied commits are
  observed.
- **SC-003**: The shared store conformance suite and the 010 purge tests pass on all three stores (in-memory, file,
  Couchbase) with no case skipped for the file store.
- **SC-004**: Each of the five flavors becomes ready with its file plus the documented environment variables; 100% of
  the misconfiguration cases of story 3 fail at startup before serving traffic, each message naming the key and the
  fix.
- **SC-005**: The provisioning command creates everything on an empty scope (exit 0), creates nothing on a second run
  (exit 0), and a Production-flavor host with startup provisioning off and a data-only credential then becomes ready.
- **SC-006**: A local restart with 10,000 stored documents is ready within 10 s; a typical command (up to 4 aggregates,
  receipt) on the file store completes with p95 ≤ 50 ms on a developer SSD.
- **SC-007**: A second host on a locked data folder fails within 5 s of start; the data folder is unchanged.
- **SC-008**: Every configuration key named in the guide, README and PCR exists in the framework's options (checked by
  review or test); a new developer reaches a ready local server following only the README.

## Assumptions

- Flavor selection uses the host environment name (`Local`, `Dev`, `Beta`, `Uat`, `Live`) because the host already
  loads one configuration file per environment name; no new selection mechanism is introduced.
- `EnvironmentId` equals the flavor name; the enforcement of FR-004 applies only to the five standard names so that a
  game can add, e.g., `qa2` with an explicit deployment environment.
- The fallback from host environment name to deployment environment is kept for the standard names (Development,
  Staging, Production) so IDE defaults and test factories keep working; owner wording "DeploymentEnvironment required"
  is met by requiring it in every flavor file and refusing any other fallback value.
- The development GM staff seed moves from the base file to the local and dev flavor files; its password is a
  development seed, not a key of `GameServer:SecretKeys` (unchanged rule: refused outside Development/Test, SEC-03).
- Couchbase defaults are the SDK defaults (KV timeout 2.5 s, compression on, SDK minimum size) except the query
  timeout, whose SDK default (75 s) is longer than the GM request timeout (30 s) and therefore defaults to a value inside
  it (FR-013). The owner's reference values (2000 ms, 2048 bytes) are valid settings the beta/uat/live files may use.
  Exact key names and bounds are chosen in the plan and recorded in PCR.
- Production requires TLS to Couchbase (SEC-01 "database uses TLS"); there is no override switch. Staging warns only, so
  existing staging clusters without TLS keep working.
- Data folder default follows the owner's reference (`../_database/dbbase`, relative to the sample host's content root);
  the `dbcache` local folder is dropped with `dbcache`.
- The file store reuses the in-memory store's semantics and adds persistence (plan option A); it remains a
  development-only store and is not evidence for Couchbase behaviour (TST-01 and TST-04 still need a real cluster).
- Local performance targets assume an SSD developer machine; the file store is not meant for load tests (use
  `start-hosts.ps1` on Couchbase).
- The provisioning mode is a switch of the host binary (not a separate tool), so it always provisions exactly the
  modules the deployed build registers.
- Bucket creation, bucket replicas, storage backend, RAM quota and RBAC user creation remain ops tasks documented in the
  guide; the full runbook belongs to slice 013.
- The runtime readiness check for missing collections (FR-032) can be done with data-only rights; if the SDK needs
  management rights for it, the plan falls back to detecting the missing collection on first use before readiness.
- Local port 5080 matches the existing Unity E2E smoke default and `start-hosts.ps1`; the sample client's default URL
  is updated if it differs.
- The POSIX script targets bash on macOS/Linux and Git Bash on Windows; the PowerShell script targets Windows
  PowerShell 5.1 and PowerShell 7.

## Out of Scope

- Audit logs Login/Payment (task 3), library packaging / NuGet / templates (task 4), HTML documentation (task 5).
- Shared cache bucket (`dbcache`), memcached or ephemeral buckets (owner Q1: add an ephemeral bucket only when a
  consumer exists).
- Per-flavor game config overlay (owner Q4 default: overlay per deployment environment).
- Hot reload of hand-edited local files, arbitrary N1QL on the file store, multiple hosts on one local data folder,
  import/export between the file store and Couchbase.
- Bucket / cluster creation, RBAC user management, backup/restore, container image, Linux service guide, deploy
  runbooks and drills (slice 013); CI pipeline (CI-01).
- Secret providers other than environment variables (Key Vault, mounted files) — OPS-08 runbook choice.
- GM frontend changes (owner 2026-10-27).
