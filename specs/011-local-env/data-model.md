# Data model — 011 local-env

Configuration keys, flavor files and the file store's on-disk format. No new Couchbase document or collection.
Decisions and rationale: [research.md](research.md).

## 1. Flavor table (FR-002) — single source: `StandardFlavors`

| Flavor (host environment name) | `GameServer:DeploymentEnvironment` | `GameServer:EnvironmentId` | `GameServer:Store` | Data | Dev tools |
| --- | --- | --- | --- | --- | --- |
| `Local` | Development | `local` | `File` | `FileStore:Folder` (sample: `../_database/dbbase`) | on |
| `Dev` | Development | `dev` | `Couchbase` | team bucket, replicas 0 on one node | on |
| `Beta` | Staging | `beta` | `Couchbase` | own bucket; IAP sandbox | off |
| `Uat` | Staging | `uat` | `Couchbase` | own bucket; IAP sandbox | off |
| `Live` | Production | `live` | `Couchbase` | separate cluster (DAT-01), TLS required | off |

Game config overlay: `config/env/<DeploymentEnvironment>` (beta and uat share `Staging`, FR-008).

## 2. Validation rules (startup, before traffic)

| Rule | Source | Message names |
| --- | --- | --- |
| `DeploymentEnvironment` ∈ {Development, Staging, Production, Test} (case-insensitive, normalized); absent → host environment name only if itself valid | `DeploymentIdentityCheck` (FR-003) | key, allowed values, expected flavor file and whether it was found, flavor table |
| `EnvironmentId` ∈ standard flavors ⇒ `DeploymentEnvironment` = table value | `DeploymentIdentityCheck` (FR-004) | both keys, expected value |
| `GameId`, `EnvironmentId` set and valid | `ModuleRegistry.ValidateOptions` (unchanged, ARC-10) | key |
| `Store` ∈ {InMemory, File} ⇒ Development or Test | `ValidateServices` via `IDevelopmentOnlyStore` (FR-007) | store, environment, fix |
| Secret keys not in any file outside Development/Test | `DeploymentChecks.ValidateSecretSources` (unchanged, OPS-08) | key, file, env var name |
| Couchbase settings (section 4) | `CouchbaseSettingsCheck` (FR-013..FR-015) | key, allowed range |
| File store settings (section 5) | `FileStoreCheck` | key / path |
| Module options (`Gm`, `Identity`, `Identity:Providers`) | existing `IValidateOptions`, now merged through `IStartupValidator` (FR-009) | key |
| Development + non-loopback listen address | warning on `ApplicationStarted` (FR-038) | addresses |

All errors of one start are reported together; warnings are logged once at startup (category `GameServer.Startup`).

## 3. Sample host configuration files

`samples/SampleHost/appsettings.json` (shared base — valid in every flavor, FR-005):

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "GameServer": { "GameId": "sample", "ConfigPath": "config" },
  "Observability": { "Logging": { "Directory": "logs", "MinimumLevel": "Information" }, "Otlp": { "Endpoint": null } }
}
```

`appsettings.Local.json`:

```json
{
  "Urls": "http://localhost:5080",
  "GameServer": { "DeploymentEnvironment": "Development", "EnvironmentId": "local", "Store": "File" },
  "FileStore": { "Folder": "../_database/dbbase" },
  "Gm": { "Bootstrap": { "Staff": [ { "StaffId": "admin", "DisplayName": "Local admin", "Password": "dev-only-change-me", "Roles": [ "Administrator" ] } ] } }
}
```

`appsettings.Dev.json`:

```json
{
  "GameServer": { "DeploymentEnvironment": "Development", "EnvironmentId": "dev", "Store": "Couchbase" },
  "Couchbase": { "ConnectionString": "couchbase://localhost", "Username": "Administrator", "BucketName": "gameserver-dev" },
  "Gm": { "Bootstrap": { "Staff": [ { "StaffId": "admin", "DisplayName": "Dev admin", "Password": "dev-only-change-me", "Roles": [ "Administrator" ] } ] } }
}
```

`appsettings.Beta.json` / `appsettings.Uat.json` (identical except `EnvironmentId` and bucket):

```json
{
  "GameServer": { "DeploymentEnvironment": "Staging", "EnvironmentId": "beta", "Store": "Couchbase" },
  "Couchbase": { "BucketName": "sample-beta", "KvTimeoutMs": 2500, "QueryTimeoutMs": 20000 }
}
```

`appsettings.Live.json`:

```json
{
  "GameServer": { "DeploymentEnvironment": "Production", "EnvironmentId": "live", "Store": "Couchbase" },
  "Couchbase": { "BucketName": "sample-live", "KvTimeoutMs": 2500, "QueryTimeoutMs": 20000 }
}
```

Required environment variables per flavor (documented in the guide; missing ones fail startup together, FR-009):

| Flavor | Required from the environment |
| --- | --- |
| local | none |
| dev | `Couchbase__Password` |
| beta, uat | `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password`, `DataProtection__MasterKey`, `Gm__Oidc__Authority`, `Gm__Oidc__ClientId`, `Gm__Oidc__ClientSecret`, `Identity__GuestPepper`, IAP keys (`Iap__Google__*`, `Iap__Apple__*` per `IapOptions`), provider keys when enabled (`Identity__Providers__PlayGames__ClientId/ClientSecret`) |
| live | as beta/uat; `Couchbase__ConnectionString` must be `couchbases://…` (+ `Couchbase__TrustCertificatePath` for a private CA) |
| provisioning run (any Couchbase flavor) | `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password` (administrative identity) only |

`samples/SampleHost/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "local": {
      "commandName": "Project",
      "launchBrowser": true,
      "launchUrl": "gm/v1/ui",
      "applicationUrl": "http://localhost:5080",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local" }
    },
    "dev-couchbase": {
      "commandName": "Project",
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5080",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Dev" }
    }
  }
}
```

`GameServer:SecretKeys` default adds `Identity:Providers:PlayGames:ClientSecret` (now 6 keys).

`GameServer:Store` gains the value `File` (`StoreKind.File`); the class default stays `InMemory` (tests).

## 4. `Couchbase` section (FR-011..FR-016)

| Key | Type | Default | Allowed / rule |
| --- | --- | --- | --- |
| `ConnectionString` | string | — | required; `couchbase://` or `couchbases://`, ≥ 1 host, comma-separated seed list, optional `?params` |
| `Username` | string | — | required |
| `Password` | string | — | required outside Development/Test; never in a file outside Development/Test (OPS-08) |
| `BucketName` | string | — | required (one bucket per environment, DAT-01) |
| `KvTimeoutMs` | int | 2500 | 100 .. 1000 × min(`Limits:PlayerTimeoutSeconds`, `Limits:GmTimeoutSeconds`) |
| `QueryTimeoutMs` | int | 20000 | 100 .. 1000 × `Limits:GmTimeoutSeconds` |
| `TrustCertificatePath` | string? | null | readable X.509 (PEM or DER); relative → content root; warning when the scheme is not TLS |
| `Compression` | bool | true | — |
| `CompressionMinSizeBytes` | int | 32 | 0 .. 20 971 520 |
| `NumKvConnections` | int | 2 | 1 .. 32 |
| `MaxKvConnections` | int | 5 | `NumKvConnections` .. 64 |
| `UseGetMulti` | bool | false | unchanged |
| `ProvisionOnStartup` | bool? | Development: true, else false | unchanged meaning; off ⇒ schema check (FR-032) |
| `ProvisioningTimeoutSeconds` | int | 600 | 30 .. 7200; provision mode only |

TLS: Production + `couchbase://` → error; Staging → warning; Development/Test → nothing (FR-014).
Scope is always `g_<gameId>` (not configurable). Reference keys of the owner's `database_settings.json`:

| Reference | Framework | Note |
| --- | --- | --- |
| `dbbase.bucketName` | `Couchbase:BucketName` | one bucket per environment |
| `dbbase.serverIp` | `Couchbase:ConnectionString` | `couchbase://ip1,ip2`; `couchbases://` for TLS |
| `dbbase.operationTimeOut` (2000) | `Couchbase:KvTimeoutMs` | durable commit timeout stays `GameServer:Transactions:KvDurableTimeoutSeconds` |
| `dbbase.compressThreshold` (2048) | `Couchbase:CompressionMinSizeBytes` | with `Compression=true` |
| `dbbase.bucketType: couchbase` | — | not an app setting; transactions need a Couchbase bucket; storage backend chosen by ops |
| `dbbase.moxiPort` | — (dropped) | Moxi removed from Couchbase Server; SDK bootstraps on standard ports |
| `dbcache` (memcached) | — (dropped, owner Q1) | no consumer; memcached buckets are deprecated and have no transactions |
| `localFolder` | `FileStore:Folder` | local flavor only |

Unused reference keys found in configuration → startup warning naming the key and the replacement (research R5).

## 5. `FileStore` section

| Key | Type | Default | Rule |
| --- | --- | --- | --- |
| `Folder` | string | — | required when `Store=File`; relative → content root; on Windows the longest document path must fit 259 characters |
| `ResetOnStart` | bool | false | pass on the command line (`--FileStore:ResetOnStart=true`); deletes the folder content after taking the lock, with the safety rules of research R9 |

## 6. File store folder

```text
<Folder>/
├── store.json                         {"format":"gameserver-file-store","version":1}
├── .lock                              exclusive while a host runs; released by the OS on exit or crash
├── journal.json                       only between the commit point and the end of a multi-document apply
├── journal.json.tmp                   partial journal (deleted at startup)
└── g_<gameId>/
    └── <collection>/
        ├── <encoded key>.json         one document
        └── <encoded key>.json.tmp     partial write (deleted at startup)
```

Document file:

| Field | Type | Rule |
| --- | --- | --- |
| `key` | string | the document key; must match the file name (decode, or SHA-256 for hashed names) |
| `expiresAtUtc` | string? | ISO-8601 UTC (`O`); null = no expiry; past at load ⇒ document dropped and file deleted |
| `content` | object | the stored document, unchanged |

Journal file (`journal.json`):

```json
{ "version": 1, "commitId": "2f0c…", "writes": [
  { "collection": "pa_wallet", "key": "p_1", "op": "put", "expiresAtUtc": null, "content": { } },
  { "collection": "receipts", "key": "rcpt::ab12…", "op": "put", "expiresAtUtc": null, "content": { } },
  { "collection": "account_deletions", "key": "deletion::p_9", "op": "remove" } ] }
```

Key → file name: literal `[a-z0-9._-]`; any other UTF-8 byte → `%XX` (uppercase hex); Windows device names get their
first character encoded; encoded stem > 120 characters → `~<base32(sha256(key))>`. Examples:

| Key | File name |
| --- | --- |
| `p_7f3k2` | `p_7f3k2.json` |
| `rcpt::9a1c…` | `rcpt%3A%3A9a1c….json` |
| `extref::iap_google::GPA.1234-5678` | `extref%3A%3Aiap_google%3A%3A%47%50%41.1234-5678.json` |
| `Keyring` vs `keyring` | `%4Beyring.json` vs `keyring.json` (distinct under case folding) |
| `con` | `%63on.json` |

Lifecycle states of a folder: *absent/empty* → created with `store.json` on first open; *store folder* → loaded;
*foreign non-empty folder* or *unknown version* → startup fails with reset guidance; *locked* → second host fails.
