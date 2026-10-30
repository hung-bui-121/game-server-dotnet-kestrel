# Contract — host command line, provisioning mode, local run scripts (011)

Operational contract published in PCR platform (new §17) and the local guide. Changing an exit code, a switch or an
output prefix below is a breaking change for pipelines and scripts (PCR-01).

## 1. Host binary

```text
<host> [--provision] [--urls <url>[;<url>…]] [--<Section>:<Key>=<value> …]
```

- `--provision` — provisioning mode (FR-029), matched in any letter case. Removed from the arguments before configuration is
  built; every other argument is ordinary command-line configuration (highest precedence). `--provision=<value>`,
  `-provision` and `/provision[=<value>]` are refused with exit 1 (the configuration provider would read them as a key and
  the host would serve instead of provisioning); the message never echoes the value.
- Flavor = host environment name (`ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT`): `Local`, `Dev`, `Beta`, `Uat`,
  `Live` (exact spelling on case-sensitive file systems), or a game-defined name with its own
  `appsettings.<Name>.json` that sets `GameServer:DeploymentEnvironment`.

### Exit codes

| Code | Serve mode | Provision mode |
| --- | --- | --- |
| 0 | graceful stop after serving | everything present (created now or before) |
| 1 | invalid configuration or startup refusal before traffic: validation errors (also a value of the wrong type), file store locked / malformed / unknown format / not readable or writable, Couchbase scope missing collections (when startup provisioning is off), a misspelled provisioning switch | invalid configuration (deployment identity, Couchbase settings, registry rules, a secret in a JSON file outside Development/Test, a misspelled switch) or the store is not Couchbase |
| 2 | — | provisioning failed or timed out (`Couchbase:ProvisioningTimeoutSeconds`); re-run after the fix continues |
| other | unhandled runtime failure (.NET default) | unhandled failure |

In both modes code 1 prints `Game server startup validation failed:` followed by one ` - <violation>` line per error on
stderr (existing `GameServerStartupException` format).

## 2. Provisioning mode (DAT-07, FR-029..FR-031)

Requires: `GameServer:GameId`, `GameServer:EnvironmentId`, a valid `GameServer:DeploymentEnvironment` (flavor file or
environment), `GameServer:Store=Couchbase`, `Couchbase:ConnectionString`, `Couchbase:Username`, `Couchbase:Password`,
`Couchbase:BucketName`. Not required: GM SSO, IAP credentials, identity pepper, data-protection master key. Starts no
web server, no job, no sweeper. Never creates buckets; never drops or alters anything.

Credential: an identity with `Bucket Admin`, `Query Manage Index` and `Data Reader` on the bucket, plus `Query System
Catalog` (7.6 syntax: `bucket_admin[<bucket>]`, `query_manage_index[<bucket>]`, `data_reader[<bucket>]`,
`query_system_catalog`). Bucket level, because the scope does not exist before the first run. `Data Reader` is needed by the
KV visibility wait and the final verification. Verified on EE 7.6.12 with a restricted user (T055): this set provisions a
fresh scope; without any one of the four roles the run exits 2. The API deployment uses a data-only identity
(`Data Reader`, `Data Writer`, `Query Select`, `Query Insert`, `Query Update`, `Query Delete` on the scope).

Secrets: as in serve mode, `Couchbase:Password` (and every `GameServer:SecretKeys` entry) must not come from a JSON file
outside Development/Test (exit 1, OPS-08).

Transient errors: a cluster-manager 503 ("Service is temporarily unavailable") on the scope listing or a scope/collection
creation is retried up to 8 attempts with backoff 0.5 s doubling to at most 5 s; other errors are not retried. An index in
state `error` fails at once (exit 2, drop it and run again); waiting for an index to be online is bounded by
`Couchbase:ProvisioningTimeoutSeconds`.

Order: scope → `txn_meta` → other collections (ordinal) → wait until collections answer KV → indexes (framework,
module, purge reference) → wait until every index is online → verification.

Standard output — one line per item, then a summary; stable prefix `provision:` and outcome words:

```text
provision: game sample, environment live (Production), bucket sample-live, scope g_sample
provision: scope g_sample present
provision: collection g_sample.txn_meta created
provision: collection g_sample.pa_profile present
…
provision: index ix_receipts_outbox_pending on receipts present
provision: index ix_profile_deletion_due on pa_profile created (online after 14 s)
provision: done: 3 created, 41 present
```

Failure (exit 2):

```text
provision: FAILED index ix_account_deletions_due on account_deletions: <cause without credentials>
provision: stopped after 12 items; fix the cause and run again (idempotent)
```

Pipeline usage:

```bash
ASPNETCORE_ENVIRONMENT=Live \
Couchbase__ConnectionString=couchbases://cb1.live,cb2.live Couchbase__Username=gs-provisioner \
Couchbase__Password="$PROVISIONER_PASSWORD" \
dotnet SampleHost.dll --provision
```

## 3. Startup schema check (FR-032)

Serve mode with `Couchbase:ProvisionOnStartup` false (default outside Development): before the server listens, every
required collection is probed with the runtime credential. Missing ones → exit 1 with:

```text
 - [couchbase] scope g_sample in bucket sample-live is missing collections txn_meta, pa_wallet. Fix: run the
   provisioning command (<host> --provision) with an administrative credential (DAT-07).
```

Indexes are not checked at startup (needs catalog rights); the provisioning report is authoritative.

## 4. Local run scripts (FR-035..FR-037)

```text
tools/local/run-local.ps1 [-Port <int>] [-Reset] [-Lan]          # Windows PowerShell 5.1 / PowerShell 7
tools/local/run-local.sh  [--port <int>] [--reset] [--lan]       # bash (macOS, Linux, Git Bash)
```

| Option | Default | Effect |
| --- | --- | --- |
| port | 5080 | host gets `--urls http://localhost:<port>` (or `http://0.0.0.0:<port>` with LAN) |
| reset | off | host gets `--FileStore:ResetOnStart=true`; the host deletes only the configured store folder after locking it and refuses anything that is not a store folder |
| lan | off | listen on all interfaces; prints a red warning block (GM UI with the development staff seed, test authentication and cheats reachable from the network) and the machine's LAN IPv4 URLs |

Behaviour: build `samples/SampleHost` (Debug), run `dotnet run --no-build --launch-profile local` from
`samples/SampleHost`, wait for `GET /health/ready` = 200 (≤ 60 s), print, keep streaming host logs, stop gracefully on
Ctrl+C (host drains, readiness 503, commits complete; the script waits up to 35 s, then kills the process tree).

Printed when ready:

```text
Local game server is ready (flavor Local, data ../_database/dbbase)
  Player API   http://localhost:5080/          (routes /api/v1/…, /v1/time)
  Health       http://localhost:5080/health/ready
  GM UI        http://localhost:5080/gm/v1/ui  (staff admin / dev-only-change-me)
  GM OpenAPI   http://localhost:5080/gm/v1/openapi/gm.json
Press Ctrl+C to stop.
```

| Exit code | Meaning (message names the cause and the fix) |
| --- | --- |
| 0 | stopped by Ctrl+C after a graceful host shutdown |
| 2 | invalid script argument |
| 3 | .NET SDK of `global.json` not found (looked at `PATH` and `~/.dotnet10`) |
| 4 | port in use — names the port and `-Port` / `--port` |
| 5 | build failed |
| 6 | host exited before ready (its own message is above: data folder locked by another host, malformed file, …) |
| 7 | readiness timeout (60 s) — prints the last 20 log lines, stops the host |
| other | host exit code after it was ready |

No path leaves a host running after the script exits.

## 5. Launch profiles (FR-034)

| Profile | Flavor | URL | Notes |
| --- | --- | --- | --- |
| `local` (default) | `Local` | `http://localhost:5080` | IDE opens `/gm/v1/ui`; same data folder as the script |
| `dev-couchbase` | `Dev` | `http://localhost:5080` | Couchbase password from the user environment variable `Couchbase__Password` |

## 6. Multi-instance script (unchanged contract, FR-033)

`tools/local/start-hosts.ps1` keeps its parameters and ports 5080+; it now sets `ASPNETCORE_ENVIRONMENT=Dev` for each
host (restored afterwards) in addition to its existing `GameServer__Store=Couchbase` and `Couchbase__*` variables.
