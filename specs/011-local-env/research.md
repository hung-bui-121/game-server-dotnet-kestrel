# Research — 011 local-env

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY]** are SDK or
runtime facts that the implementer confirms on the pinned versions (CouchbaseNetClient 3.10.0, .NET SDK 10.0.403,
Couchbase EE 7.6.12) before coding against them; each has a stated fallback.

Code facts this research relies on (verified 2026-10-30 on branch `011-local-env`, base `010-jobs-account-deletion`):

- `GameServerBuilderExtensions.AddGameServer` (`GameServerBuilder.cs:118`) sets `DeploymentEnvironment` from
  `IHostEnvironment.EnvironmentName` whenever `GameServer:DeploymentEnvironment` is absent, without checking the value.
  `GameServerOptions.IsDevelopment/IsProduction/IsTest` compare case-insensitively, so `Local` is "neither".
  `FileGameConfigLoader.Load(root, deploymentEnvironment)` uses the raw string as folder name `config/env/<value>`
  (case-sensitive on Linux).
- `GameServerOptions.Store` is `StoreKind { InMemory, Couchbase }`, default `InMemory`; only `samples/SampleHost/Program.cs`
  reads it. `SecretKeys` lists `Identity:GuestPepper`, `Couchbase:Password`, `Iap:Google:ServiceAccountPrivateKeyPem`,
  `DataProtection:MasterKey`, `Gm:Oidc:ClientSecret` — **not** `Identity:Providers:PlayGames:ClientSecret`.
- Startup validation is split: `UseGameServer` collects registry, services, secret-source, shutdown, data protection,
  gate and endpoint errors into one `GameServerStartupException`; module options (`GmOptions`, `IdentityOptions`,
  `IdentityProviderOptions`) use `ValidateOnStart()` and fail later, inside `app.StartAsync`; `AddObservability` and
  `AddCouchbaseDocumentStore` (missing bucket) throw during composition, outside the `try` in `Program.cs`.
- `ValidateServices` refuses any `IDevelopmentOnlyStore` outside Development/Test with an "in-memory store" message.
- `CouchbaseOptions` has `ConnectionString` (default `couchbase://localhost`), `Username`, `Password`, `BucketName`,
  `UseGetMulti`, `ProvisionOnStartup`. `CouchbaseConnection.ConnectAsync` sets only `KvDurabilityTimeout`, credentials,
  serializer, logging and the transactions config (metadata collection `g_<gameId>.txn_meta`).
- `CouchbaseProvisioner` (hosted service + `IStoreInitializer`) creates scope, `RequiredCollections()` (**`txn_meta`
  appended last**), framework indexes, module indexes and purge reference indexes with `CREATE INDEX … IF NOT EXISTS`;
  it reports nothing per item and only runs when `ProvisionOnStartup ?? IsDevelopment`. Otherwise
  `EnsureInitializedAsync` returns at once and nothing checks the schema.
- `KeyRingService.StartingAsync`, `GmBootstrapper` and `LeaseJobRunner` await `IStoreInitializer.EnsureInitializedAsync`
  before touching the store; `KeyRingService` then reads the store, so an unreachable or unprovisioned cluster already
  prevents the host from starting today (with an SDK error, not a useful message).
- `InMemoryDocumentStore` (476 lines) mutates `_documents` in exactly these places, all under `_lock`
  (`System.Threading.Lock`): `TryCommit`, `IKeyValueStore.InsertAsync/ReplaceAsync/RemoveAsync`, `InsertIfAbsentAsync`,
  `MarkOutboxRelayedAsync`, `MarkOutboxFailedAsync`, `Seed`, and `Live()` (lazy removal of expired entries). Expiry is
  stored as absolute `_clock.UtcNow + expiry`; transactional writes carry no expiry. Versions are a process-local
  sequence (`_versionSequence`), not persisted meaning.
- `samples/SampleHost` already references `GameServer.Platform.Testing` (for the in-memory store), so that assembly
  already ships in the sample host's output.
- Test projects that reference `SampleHost` get its `appsettings.json` copied into their output; `TestHost` uses
  `ContentRootPath = AppContext.BaseDirectory`, so every `TestHost` in those projects reads the SampleHost base file
  (today: `EnvironmentId=dev`, `Store=InMemory`, GM staff seed `admin`). `SsoTests` comments on this dependency.
- `TestHost.DefaultSettings()` uses `EnvironmentId = "dev"`; several tests switch `DeploymentEnvironment` to `Test`,
  `Staging` or `Production` on top of it (`ProviderExtensionTests`, `StartupValidationTests`, `ConfigAndCheatTests`).
- `WebApplicationFactory<Program>` is used only by `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs`
  (environment `Development`, content root = SampleHost project directory).
- `tools/local/start-hosts.ps1` sets `GameServer__Store`, `Couchbase__*`, `Observability__*`, never
  `ASPNETCORE_ENVIRONMENT` (host default: Production) and relies on the base file's `DeploymentEnvironment=Development`;
  it runs `dotnet <copy>\SampleHost.dll --urls http://localhost:<port>` with working directory `samples/SampleHost`.
- Unity sample: `SampleGameClient.serverUrl = "http://localhost:5000/"` (code default **and** serialized in
  `Assets/Scenes/SampleGameClient.unity` line 151), `Editor/SmokeBuild.cs` and `sdk/unity/ci/smoke-build.ps1` default
  `http://localhost:5000/`; `tools/GameServer.LoadTest` and the real-env guide already use 5080.
- No CI pipeline exists (`.github/` absent; CI-01 open). `BannedSymbols.txt` bans `DateTime(Offset).(Utc)Now` and
  sync-over-async in framework code (including `GameServer.Platform.Testing`).

---

## R1 — Flavor selection and configuration layering (FR-001, FR-002, FR-008, FR-033)

**Decision.** A flavor is the **host environment name** (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`) with the
spelling `Local`, `Dev`, `Beta`, `Uat`, `Live`; the host loads `appsettings.{Flavor}.json` over `appsettings.json` (built-in
`WebApplicationBuilder` behaviour, no new mechanism). Effective precedence, lowest to highest:

1. `appsettings.json` (shared base, R3)
2. `appsettings.{Flavor}.json`
3. environment variables (`GameServer__Store`, `Couchbase__Password`, …) — secrets only come from here (OPS-08)
4. command-line arguments (`--urls`, `--FileStore:ResetOnStart=true`, …)

File names are case-sensitive on Linux; the startup message for a missing deployment environment (R2) names the file
it expected (`appsettings.Live.json`) and whether it was found, so `ASPNETCORE_ENVIRONMENT=live` on Linux fails with a
readable cause instead of a silent fallback.

The game config overlay stays `config/env/<DeploymentEnvironment>` (FR-008, owner Q4): beta and uat share `Staging`.
Because `DeploymentEnvironment` is now normalized to its canonical casing (R2), the overlay folder lookup is
stable on case-sensitive file systems.

**Port.** The local flavor file sets the generic host key `"Urls": "http://localhost:5080"`. Not `Kestrel:Endpoints`:
Kestrel endpoint configuration overrides `--urls`/`ASPNETCORE_URLS` and would break `start-hosts.ps1` and the script's
`-Port`. `--urls` (command line) overrides the file value. **[VERIFY]** In `WebApplicationBuilder`, `ASPNETCORE_URLS`
(prefixed environment provider, added before the JSON files) has *lower* precedence than `Urls` in a JSON file; the
launch profile `applicationUrl` sets `ASPNETCORE_URLS`, so the plan makes the launch profile and the local file carry the
**same** value (no ambiguity whichever wins), and the scripts use `--urls`. A test asserts that `--urls` wins over the
file (R12).

**Alternatives.** (a) A new `GameServer:Flavor` key selecting a file — duplicates the host environment mechanism and
needs custom file loading. (b) One file per flavor *and* per deployment environment — more files, no extra value.
(c) `Kestrel:Endpoints` for the local port — rejected above.

**Trade-off.** Flavor names are tied to file names; a game adding `qa2` adds `appsettings.Qa2.json` and sets
`DeploymentEnvironment` explicitly (R2).

---

## R2 — Deployment environment resolution and flavor validation (FR-003, FR-004, FR-009, FR-010)

**Decision.**

- Canonical values live in Platform:

  ```csharp
  namespace GameServer.Platform;

  public static class DeploymentEnvironments
  {
      public const string Development = "Development";
      public const string Staging = "Staging";
      public const string Production = "Production";
      public const string Test = "Test";

      public static IReadOnlyList<string> All { get; }

      /// <summary>Case-insensitive match against <see cref="All"/>; returns the canonical spelling.</summary>
      public static bool TryNormalize(string? value, out string normalized);
  }

  /// <summary>The five standard flavors (FR-002); games may add others with an explicit deployment environment.</summary>
  public static class StandardFlavors
  {
      /// <summary>local→Development, dev→Development, beta→Staging, uat→Staging, live→Production (keys = EnvironmentId).</summary>
      public static IReadOnlyDictionary<string, string> DeploymentEnvironmentById { get; }

      /// <summary>"local→Development, dev→Development, beta→Staging, uat→Staging, live→Production" for messages.</summary>
      public static string Describe();
  }
  ```

- `AddGameServer` resolves the effective value once and records how:

  ```csharp
  namespace GameServer.Platform.Web.Hosting;

  internal sealed record DeploymentEnvironmentResolution(
      string? ConfiguredValue,        // GameServer:DeploymentEnvironment as written, null when absent
      string HostEnvironmentName,
      string Effective,               // canonical when valid; the raw value otherwise (fail-closed: not Development)
      bool IsValid,
      bool FlavorFileFound);          // appsettings.{HostEnvironmentName}.json was a configuration source
  ```

  Rules: configured value → `TryNormalize` (valid → canonical; invalid → keep raw, `IsValid=false`); absent → the host
  environment name only when it is itself a deployment environment (`Development`, `Staging`, `Production`, `Test`, any
  casing → canonical), otherwise `IsValid=false` with `Effective = HostEnvironmentName`. An invalid value never equals
  `Development`/`Test`, so registration decisions taken before validation (test auth, cheat module, OpenAPI, fake
  verifiers) fail closed.
- `DeploymentIdentityCheck : IStartupCheck` (R4) reports:
  - invalid/missing deployment environment: `[deployment] GameServer:DeploymentEnvironment is not set and the host
    environment name 'Local' is not a deployment environment (allowed: Development, Staging, Production, Test).
    Fix: set it in appsettings.Local.json (found: yes|no, content root …). Standard flavors: local→Development, …`;
  - FR-004: `EnvironmentId` is a key of `StandardFlavors` (ordinal, lowercase) and the effective deployment environment
    differs → error naming both keys and the expected value. `Test` is **not** exempt.
- `GameId`/`EnvironmentId` required: unchanged (`ModuleRegistry.ValidateOptions`, ARC-10).
- FR-010: unchanged code paths (`AllowsDevelopmentTools`), now fed by a validated value.

**Consequence for tests.** `TestHost.DefaultSettings()["GameServer:EnvironmentId"]` changes from `dev` to `test` (not a
standard flavor), so tests that switch the deployment environment keep starting. Tests that asserted the literal
`dev` *through a host* are updated (grep list in tasks). Unit tests constructing `new GameServerOptions { EnvironmentId =
"dev" }` are unaffected (validation runs in the host only). `TestHost.StartAsync` gains an optional
`environmentName` parameter for the R2 tests.

**Alternatives.** (a) Remove the fallback and require the key always — breaks IDE defaults and every
`WebApplicationFactory`/`TestHost` (spec assumption keeps it). (b) Exempt `Test` from FR-004 — a hidden rule; changing
the test default is cleaner. (c) Validate in `AddGameServer` and throw — would report deployment errors alone, not "every
key at once" (FR-009).

---

## R3 — Shared base file, flavor files, GM seed, secret repository test (FR-005, FR-006, FR-009)

**Decision.**

- `samples/SampleHost/appsettings.json` keeps only values valid in every flavor: `Logging`, `AllowedHosts`,
  `GameServer:GameId = "sample"` (a deployment constant, ARC-10), `GameServer:ConfigPath`, `Observability:Logging`,
  `Observability:Otlp:Endpoint = null`. It loses `DeploymentEnvironment`, `EnvironmentId`, `Store` and
  `Gm:Bootstrap:Staff` (arrays merge by index, so a flavor could not remove it).
- Five flavor files (full content in [data-model.md](data-model.md)): `appsettings.Local.json`, `appsettings.Dev.json`,
  `appsettings.Beta.json`, `appsettings.Uat.json`, `appsettings.Live.json`. Each sets `DeploymentEnvironment`,
  `EnvironmentId` and `Store` explicitly. Deployment-specific values (Couchbase connection string and username,
  GM OIDC authority/client id, IAP identifiers) are **not** in beta/uat/live files: they come from environment variables,
  so a deployment that forgets them fails on the required key instead of connecting to a placeholder.
- GM staff seed (`admin`, `Administrator`, development password) lives in `appsettings.Local.json` and
  `appsettings.Dev.json` only. The seed password is not a `SecretKeys` entry (spec assumption); it is accepted because
  `dev` is Development (test authentication by header is on there anyway — the larger exposure). The guide states that
  the dev environment must be network-restricted.
- `GameServerOptions.SecretKeys` gains `Identity:Providers:PlayGames:ClientSecret` (gap found: a real secret missing from
  the OPS-08 list). Stricter only; no flavor file sets it.
- Repository test `SampleHostConfigurationFileTests` (in `tests/SampleGame.Tests`, which already references SampleHost)
  reads every `samples/SampleHost/appsettings*.json` from the source tree (not the copied output) with a
  `ConfigurationBuilder`, and asserts:
  1. no key of `new GameServerOptions().SecretKeys` has a non-empty value in any file (FR-006, ACC-48);
  2. the base file has no `GameServer:DeploymentEnvironment`, `GameServer:EnvironmentId`, `GameServer:Store`,
     `Gm:Bootstrap` (FR-005);
  3. each standard flavor file sets `EnvironmentId` = flavor name and `DeploymentEnvironment` = the R2 table value, and
     `Store` = `File` for local, `Couchbase` otherwise (FR-002);
  4. only `appsettings.Local.json` sets `Urls`, and it is a loopback URL on port 5080 (FR-033).

**Impact on existing consumers of the base file.** `SampleHostSmokeTests` switches to the local flavor (R12);
`SsoTests.Bootstrap_managers_are_invited_when_the_deployment_has_none` drops its override of the seeded admin's role
(the seed is gone from the copied base file); `start-hosts.ps1` sets `ASPNETCORE_ENVIRONMENT=Dev` (R12).

**Alternatives.** (a) Keep development values in the base file and override them in beta/uat/live — fails open when a
flavor file is forgotten and cannot remove arrays. (b) `appsettings.Development.json` as the local file — would make
every IDE default and test factory run the local flavor implicitly.

---

## R4 — One startup report: checks contributed by components, options validators merged (FR-009, FR-013..FR-015, FR-038)

**Decision.** A small extension point in Platform lets store packages and the host contribute startup errors and
warnings without Platform.Web referencing them:

```csharp
namespace GameServer.Platform;

/// <summary>Contributes startup violations (fail before traffic) and warnings (logged once). Must not throw or do I/O
/// beyond reading local files named by configuration.</summary>
public interface IStartupCheck
{
    void Check(StartupCheckResult result);
}

public sealed class StartupCheckResult
{
    public List<string> Errors { get; }
    public List<string> Warnings { get; }
}
```

Registered checks in this slice: `DeploymentIdentityCheck` (Platform.Web, by `AddGameServer`),
`CouchbaseSettingsCheck` (by `AddCouchbaseDocumentStore`), `FileStoreCheck` (by `AddFileDocumentStore`).

`UseGameServer` changes:

1. Runs every `IStartupCheck` and adds their errors to the existing list; logs warnings through
   `ILogger` category `GameServer.Startup` before mapping endpoints.
2. Calls `IStartupValidator.Validate()` (the service behind `ValidateOnStart`, .NET 8+) inside a `try`, and merges
   `OptionsValidationException.Failures` (single or inside `AggregateException`) into the list, so GM SSO, identity
   pepper and provider settings are reported together with Couchbase and data-protection errors (FR-009 "every missing
   key at once"). The host runs it again at `StartAsync`; validated options are cached, so the second run is free.
3. The `IDevelopmentOnlyStore` message is generalized: `[deployment] the {Store} store is configured in '{env}'. Fix: set
   GameServer:Store=Couchbase and the Couchbase settings; the InMemory and File stores are for Development/Test only
   (FR-007).`
4. `AddCouchbaseDocumentStore` no longer throws on a missing bucket; `CouchbaseSettingsCheck` reports it.
   `Program.cs` still wraps composition in a `try` because `AddObservability` throws by design.

**Alternatives.** (a) Static validation calls per component inside `UseGameServer` — Platform.Web would have to
reference Couchbase and the file store. (b) `IValidateOptions<CouchbaseOptions>` — `CouchbaseOptions` is bound in
`Program.cs`, not through `IOptions`, and validators cannot emit warnings.

---

## R5 — Couchbase settings for SDK 3.10 (FR-011..FR-016)

**Decision.** New `CouchbaseOptions` members, mapped 1:1 to `ClusterOptions` in one internal factory so the mapping is
unit-testable without a cluster:

```csharp
namespace GameServer.Platform.Couchbase;

public sealed class CouchbaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;   // was "couchbase://localhost" (fail-open default removed)
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public bool UseGetMulti { get; set; }
    public bool? ProvisionOnStartup { get; set; }

    public int KvTimeoutMs { get; set; } = 2500;
    public int QueryTimeoutMs { get; set; } = 20000;
    public string? TrustCertificatePath { get; set; }
    public bool Compression { get; set; } = true;
    public int CompressionMinSizeBytes { get; set; } = 32;
    public int NumKvConnections { get; set; } = 2;
    public int MaxKvConnections { get; set; } = 5;
    public int ProvisioningTimeoutSeconds { get; set; } = 600;

    /// <summary>True for the <c>couchbases://</c> scheme.</summary>
    public bool UsesTls { get; }

    public static string ScopeName(string gameId);
}

internal static class CouchbaseClusterOptionsFactory
{
    public static ClusterOptions Create(
        CouchbaseOptions options,
        GameServerOptions gameServer,
        ITypeSerializer serializer,
        X509Certificate2Collection? trustedRoots,
        ILoggerFactory loggerFactory);
}
```

| Key | Default | Allowed | SDK property (**[VERIFY]** names on 3.10) |
| --- | --- | --- | --- |
| `ConnectionString` | — (required) | `couchbase://h1[,h2…][?params]` or `couchbases://…` | `ConnectionString` |
| `Username` | — (required) | non-empty | `WithPasswordAuthentication` |
| `Password` | — | required outside Development/Test; environment only (OPS-08) | idem |
| `BucketName` | — (required) | non-empty | — |
| `KvTimeoutMs` | 2500 (SDK default) | 100 .. `1000 × min(PlayerTimeoutSeconds, GmTimeoutSeconds)` (default 10 000) | `KvTimeout` |
| `QueryTimeoutMs` | 20 000 | 100 .. `1000 × GmTimeoutSeconds` (default 30 000) | `QueryTimeout` |
| `TrustCertificatePath` | null (system trust) | readable PEM/DER X.509 file; relative to content root | see TLS below |
| `Compression` | true (SDK default) | bool | `Compression` |
| `CompressionMinSizeBytes` | 32 (SDK default **[VERIFY]**) | 0 .. 20 971 520 | `CompressionMinSize` |
| `NumKvConnections` | 2 (SDK default) | 1 .. 32 | `NumKvConnections` |
| `MaxKvConnections` | 5 (SDK default) | `NumKvConnections` .. 64 | `MaxKvConnections` |
| `ProvisionOnStartup` | Development: true; else false | bool | — |
| `ProvisioningTimeoutSeconds` | 600 | 30 .. 7200 (provision mode only) | — |

Query timeout default is 20 s, not the SDK's 75 s: queries serve GM pages (30 s endpoint timeout) and jobs; the player
API never queries. The owner's reference values (`operationTimeOut: 2000`, `compressThreshold: 2048`) are valid
values beta/uat/live files may set.

**TLS (SEC-01, FR-014).** TLS is selected by the `couchbases://` scheme (SDK enables TLS from the scheme). With
`TrustCertificatePath`, the factory trusts **only** that CA for both KV and HTTP connections: a remote-certificate
callback that builds an `X509Chain` with `ChainPolicy.TrustMode = CustomRootTrust` and the configured roots, keeping
host-name validation. **[VERIFY]** whether 3.10 offers a built-in trusted-root option (preferred if present); otherwise
use `KvCertificateCallbackValidation` / `HttpCertificateCallbackValidation`. Rules in `CouchbaseSettingsCheck`:
Production + `couchbase://` → error; Staging → warning; Development/Test → nothing. `TrustCertificatePath` with
`couchbase://` → warning ("ignored without TLS"). There is no override switch (spec assumption).

**Legacy reference keys (FR-012, FR-015).** `CouchbaseSettingsCheck` reads `IConfiguration` and warns (never fails):

| Found key (case-insensitive) | Warning says |
| --- | --- |
| `Couchbase:MoxiPort`, `dbbase:moxiPort` | Moxi no longer exists; the SDK bootstraps from the connection string on standard ports |
| `Couchbase:BucketType`, `dbbase:bucketType` | not an application setting; transactions need a Couchbase bucket, created by ops |
| `Couchbase:DbCache`, top-level `dbcache` | dropped (owner Q1): no consumer; memcached buckets cannot run transactions |
| `Couchbase:ServerIp`, `dbbase:serverIp` | renamed: use `Couchbase:ConnectionString` |
| `Couchbase:OperationTimeOut`, `dbbase:operationTimeOut` | renamed: use `Couchbase:KvTimeoutMs` |
| `Couchbase:CompressThreshold`, `dbbase:compressThreshold` | renamed: use `Couchbase:CompressionMinSizeBytes` |
| `dbbase:bucketName` | renamed: use `Couchbase:BucketName` |
| `dbbase:localFolder` | renamed: use `FileStore:Folder` |

**FR-016.** Unchanged: one `CouchbaseConnection` per process; the provisioner, jobs, sweeper and store share it; the
transaction metadata collection stays `g_<gameId>.txn_meta` everywhere.

**Alternatives.** (a) Pass-through of raw SDK settings (`Couchbase:Sdk:*`) — unvalidated, couples the contract to the
SDK. (b) Keep the localhost default — a Production deployment missing the variable would try `localhost`.

---

## R6 — File store architecture (FR-007, FR-017, FR-018, FR-026..FR-028)

**Decision. Option A**: the file store is an `InMemoryDocumentStore` with an **internal persistence seam**, owned by a
`FileDocumentStore` that loads the folder into it at startup and persists every mutation before it becomes visible.
All store semantics (transactions, conflicts, CAS, expiry, TransactionOnly, undeclared collections, paged/outbox/due/
reference queries, `RemoveAsync`) stay in the one class that already passes the conformance suite.

Seam (internal to `GameServer.Platform.Testing`; no public API change of `InMemoryDocumentStore`):

```csharp
namespace GameServer.Platform.Testing;

/// <summary>Receives every committed change under the store lock, before it is visible. Throws to refuse the write
/// (nothing applied). Returning normally means the change is durable.</summary>
internal interface IDocumentPersistence
{
    void Persist(IReadOnlyList<DocumentChange> changes);
}

/// <summary>Content null = removal. ExpiresAt is absolute UTC (DAT-03).</summary>
internal readonly record struct DocumentChange(DocumentKey Key, JsonObject? Content, DateTimeOffset? ExpiresAt);

public sealed class InMemoryDocumentStore
{
    internal InMemoryDocumentStore(IClock clock, IDocumentPersistence persistence);

    /// <summary>Replaces the committed state with loaded documents and opens the store; before this, every access throws.</summary>
    internal void LoadCommitted(IEnumerable<(DocumentKey Key, JsonObject Content, DateTimeOffset? ExpiresAt)> documents);
}
```

Every mutation path listed in the code facts calls `Persist` once, with the full change set, after its own checks
(version match, existence, `EnsureWritable`) and before updating `_documents`. `Live()` expiry removal does not
persist (expired files are filtered at load, R9). With no persistence attached (the public constructor), behaviour and
cost are unchanged — the 600+ existing tests are the regression net. A persistence failure inside `TryCommit` surfaces
from `RunTransactionAsync` as `TransactionFailedException` (definitely not committed); on KV paths the `IOException`
propagates (mapped to `INTERNAL` by `ExceptionMiddleware`).

Public types (all in `GameServer.Platform.Testing`, folder `FileStore/`):

```csharp
namespace GameServer.Platform.Testing;

public sealed class FileStoreOptions
{
    public const string SectionName = "FileStore";
    public string Folder { get; set; } = string.Empty;   // relative → content root (FR-017)
    public bool ResetOnStart { get; set; }               // command line only by convention (R9)
}

public sealed record FileStoreSettings(string Folder, string GameId, bool ResetOnStart, string ContentRoot);

/// <summary>Development-only document store backed by a folder of JSON files (one per document).</summary>
public sealed class FileDocumentStore : IStoreInitializer, IHostedService, IDisposable
{
    public FileDocumentStore(FileStoreSettings settings, IClock clock, ILogger<FileDocumentStore> logger);

    /// <summary>The store to register; refuses access until <see cref="Open"/> ran.</summary>
    public InMemoryDocumentStore Store { get; }

    public string Folder { get; }

    /// <summary>True after a write failed past its commit point; every later write fails until restart (R8).</summary>
    public bool IsFaulted { get; }

    /// <summary>Lock, format check, optional reset, journal recovery, load. Idempotent. Throws GameServerStartupException.</summary>
    public void Open();

    public Task EnsureInitializedAsync(CancellationToken cancellationToken);
    public Task StartAsync(CancellationToken cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken);
    public void Dispose();   // releases the folder lock
}

public static class FileStoreServiceCollectionExtensions
{
    /// <summary>Binds FileStore, resolves the folder against the content root, registers the store, initializer,
    /// hosted service, health probe and FileStoreCheck.</summary>
    public static IServiceCollection AddFileDocumentStore(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment);
}
```

Internal: `FileKeyCodec` (R7), `FileStoreFolder` (lock, format file, paths, reset safety), `FileCommitWriter :
IDocumentPersistence` (R8), `FileStoreLoader` (R9), `FileStoreHealthProbe : IStoreHealthProbe` (unhealthy when faulted,
else delegates to the default probe), `FileStoreCheck : IStartupCheck` (folder set, path budget). Crash points for tests
via `InternalsVisibleTo("GameServer.Platform.Tests")`.

**Refusal outside Development/Test (FR-007, FR-027).** `InMemoryDocumentStore` implements `IDevelopmentOnlyStore`, so the
existing `ValidateServices` rule refuses the file store in Staging/Production. The check runs in `UseGameServer`, before
`StartAsync`, so a misconfigured Staging/Production host never locks or reads the folder (`Open` runs in
`EnsureInitializedAsync`, first awaited by `KeyRingService.StartingAsync`).

**Project placement.** `GameServer.Platform.Testing`, not a new `GameServer.Platform.LocalStore`:
- The seam stays `internal` — no new public extension point on `InMemoryDocumentStore` for games to misuse.
- No new project, solution entry or architecture-test assembly list change.
- "Not shipping it in production" is not achievable either way: FR-001 deploys **one build** to every flavor and the
  flavor is chosen at run time, and the sample host already ships `GameServer.Platform.Testing` for the in-memory store.
  The guard is the runtime refusal above. When packaging (task 4) happens, `Testing` can be split without changing the
  public types' namespace.

**Alternatives.** (B) A separate store reading/writing files directly — re-implements transactions and four query
kinds; semantics drift from Couchbase. (C) Couchbase in Docker — not "a file", no Docker on dev machines (ADR-0001).
(A') a public `ICommitObserver` hook on `InMemoryDocumentStore` — widens the public surface for one internal consumer.

**Trade-off.** Disk writes run synchronously under the in-memory store lock (Complexity Tracking): simple, ordered,
correct; serializes all writes of one local host, which is irrelevant at developer load (SC-006).

---

## R7 — On-disk layout and key encoding (FR-021, FR-025)

**Decision.**

```text
<Folder>/                                   default for the sample host: <content root>/../_database/dbbase
├── store.json                              {"format":"gameserver-file-store","version":1}
├── .lock                                   held open exclusively while a host runs (R9)
├── journal.json                            exists only between the commit point and the end of a multi-document apply
└── g_<gameId>/<collection>/<file>.json     one document
```

Document file (UTF-8, indented, hand-editable while the host is stopped):

```json
{
  "key": "p_7f3k2",
  "expiresAtUtc": null,
  "content": { "...": "stored JSON exactly as the store holds it" }
}
```

`expiresAtUtc`: ISO-8601 UTC (`"O"` format) or null. The original key is stored in the file; load verifies it against
the file name.

**Key → file name (`FileKeyCodec`).** Percent-encoding over UTF-8 with a restricted literal set:

- literal: `a-z`, `0-9`, `.`, `_`, `-`; every other byte (uppercase letters, `:`, `/`, `\`, `%`, spaces, non-ASCII)
  becomes `%XX` with **uppercase** hex. Uppercase letters never appear as literals and hex pairs always follow `%`, so two
  different keys never map to names equal under case folding (Windows, default macOS);
- a key whose part before the first `.` equals a Windows reserved device name (`con`, `prn`, `aux`, `nul`, `com1`–`com9`,
  `lpt1`–`lpt9`, case-insensitive) gets its first character encoded (`con` → `%63on`); a key ending in `.` or space is
  fine because `.json` is appended;
- empty key → `%` alone is invalid, so the empty key is refused by the store (keys are never empty in the framework);
- if the encoded stem exceeds **120** characters the name is `~` + lowercase base32 SHA-256 of the UTF-8 key (52
  characters). `~` is not a literal, so hashed names never collide with encoded ones. These names are reversible through
  the `key` field (load checks `hash(key) == name`).

Decoding is the exact inverse; `TryDecode` refuses non-canonical forms (lowercase hex, encoded literals) so a hand-renamed
file fails load instead of shadowing another key.

**Path budget (Windows).** `FileStoreCheck` computes `len(folder) + len("/g_<gameId>/") + max(len(collection)) + 1 +
126` and fails on Windows when it exceeds 259 ("move the data folder to a shorter path"); collection names come from the
registry, which already restricts them.

**Format version.** `store.json` is created in an empty folder. Startup fails with reset guidance when it is missing in a
non-empty folder (not a store folder), names another format, or has an unknown version.

**Git.** `.gitignore` gains `_database/`.

**Alternatives.** One file per collection (owner Q5 chose per document); base64url names (unreadable, and
case-sensitive — collides on Windows); hashed names for all keys (unreadable).

---

## R8 — Durability and multi-document atomicity (FR-019, FR-020, SC-002)

**Decision.** `FileCommitWriter.Persist(changes)` runs under the store lock:

- **One change** (KV writes, outbox marks, single-document transactions): write `<file>.json.tmp`, flush, rename over
  `<file>.json` (`File.Move(overwrite: true)`); removal = delete. **Commit point = the rename/delete.**
- **Several changes** (multi-document transactions):
  1. write `journal.json.tmp` = `{"version":1,"commitId":"<guid>","writes":[{collection,key,op:"put"|"remove",
     expiresAtUtc,content}]}`, flush;
  2. rename to `journal.json` — **commit point**;
  3. apply every write as in the one-change case;
  4. delete `journal.json`.

**Failure handling.** Before the commit point: delete temporaries, throw → nothing applied, the transaction is reported
failed (not committed). After the commit point: the change is durable (the journal replays at restart), so `Persist`
returns normally (memory is updated, the caller sees success) **and** the writer enters the **faulted** state: every
later `Persist` throws, `FileStoreHealthProbe` reports unhealthy (readiness 503), one error log names the file and the
cause; a restart replays the journal. Rename/delete on Windows retries up to 5 × 20 ms on sharing violations
(antivirus, indexer) before counting as a failure.

**Recovery at `Open`.** Delete every `*.tmp` under the folder; if `journal.json` exists and parses, re-apply every write
(idempotent) and delete it; if it does not parse, fail startup naming it ("delete it to drop the last commit, or
reset").

**Durability model (decided).** "Persisted" = written and flushed to the operating system before success (no fsync).
This survives process death at any instant — Ctrl+C, debugger stop, `kill -9`, Task Manager — which is the failure
model of SC-002 ("normal stop or forced kill"). An OS crash or power loss may lose the last commits or leave a torn file;
torn files fail startup by name (never read as documents), but a multi-document commit could be partially present after
power loss. Documented as a limit in PCR and the guide (reset).

**Why not fsync.** A per-commit fsync of the journal plus every applied file costs N+1 flushes (≈ 5–60 ms per commit on
Windows), threatening the p95 50 ms target (SC-006) for a guarantee no developer scenario needs; an fsync of the journal
alone is not sufficient because the journal is deleted after apply.

**Crash testing.** Internal crash points (`FileCommitStep`: `JournalTempWritten`, `JournalCommitted`,
`DocumentTempWritten(i)`, `DocumentReplaced(i)`, `JournalDeleted`, `SingleTempWritten`) let a test abort the writer
mid-commit without any further disk operation, release the lock handle (as the OS would) and reopen the folder; the test
asserts every document of the commit is present (crash after the commit point) or absent (before). Plus a torn
`journal.json.tmp` and a torn document `.tmp`. An opt-in real-process kill test runs the built SampleHost in the local
flavor on a temp folder, kills it during a burst of commands N times and checks receipt/aggregate consistency after each
restart (`GAMESERVER_KILL_TEST=1`).

---

## R9 — Lock, load, expiry, malformed files, reset, performance (FR-022..FR-025, FR-028, SC-006, SC-007)

**Lock (FR-023).** `.lock` opened with `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.None` for the
lifetime of `FileDocumentStore`. On Windows this is a sharing lock; on Unix .NET applies an advisory `flock` for
`FileShare.None`. Both are released by the OS when the process dies, so a crashed host never blocks the next start. A
second host fails immediately (SC-007): `[filestore] the data folder '<abs path>' is used by another host. Stop it, or
run this host with another FileStore:Folder.` Network shares: semantics depend on the share; not a goal (edge case).

**Load (`Open`).** Order: lock → `store.json` check/create → reset (if requested) → recovery (R8) → enumerate
`g_<gameId>/<collection>/` for the declared collections (all folders when the store is unrestricted, as in unit tests);
undeclared folders are ignored with one information log → parse files in parallel (`Parallel.ForEach`, degree =
processor count) → validate shape (`key` string, `content` object, `expiresAtUtc` null or ISO-8601 UTC) and name ↔ key →
drop and delete expired documents (clock = `IClock`, including any development offset) → `Store.LoadCommitted(...)`.
All malformed files are collected and reported at once (first 20 named, count of the rest): `[filestore] '<path>' is
not a valid document (<reason>). Fix it or delete it while the host is stopped, or reset the local data.` Nothing is
skipped silently (FR-024). Log: `Loaded {Count} documents from {Folder} in {Ms} ms`.

**Expiry (FR-022, DAT-03).** Absolute UTC in the file; expired documents are never returned (`Live()`/`IsLive` in
memory, filtered at load) and are deleted from disk at the next start or overwritten earlier by a new write of the same
key. No background sweeper (dev store).

**Everything persists (FR-028).** Sessions, GM sessions, `dataprotection/keyring`, leases, purge records, receipts and
projections are ordinary documents of the same store. In Development the key ring is stored **unencrypted** (no master
key); the guide warns that `_database/` holds key material and guest credentials and must not be shared.

**Reset (FR-025, FR-036).** Deleting the folder by hand is always a reset. `FileStore:ResetOnStart=true` (passed by the
script as a command-line argument) makes `Open`, **after taking the lock**, delete the folder's content except `.lock`
and recreate `store.json`, refusing when: the folder is not a store folder (no `store.json` of this format) and not
empty; the folder is a file-system root, the user profile directory, the content root or one of its ancestors; or
contains a `.git` entry. Taking the lock first means a running host blocks a reset.

**Performance (SC-006).** Expected on a developer SSD: load 10 000 documents in 1–4 s (parallel; Windows Defender scanning
dominates — the guide suggests excluding the data folder); one-document write ≈ 0.5–2 ms; a 6-document commit (4
aggregates, receipt, claim) ≈ 5–15 ms. An opt-in performance test (`GAMESERVER_PERF=1`) asserts load ≤ 10 s for 10 000
documents and p95 ≤ 50 ms over 500 commits and records the numbers in PCR.

---

## R10 — Provisioning mode of the host binary (FR-029..FR-031, SC-005)

**Decision.** `<host> --provision` (e.g. `dotnet SampleHost.dll --provision`) provisions and exits; same binary, same
module registration, same configuration layering as a normal start.

```csharp
namespace GameServer.Platform.Web.Hosting;

public enum HostMode { Serve, Provision }

public sealed record GameServerCommandLine(HostMode Mode, string[] HostArgs)
{
    public const string ProvisionSwitch = "--provision";

    /// <summary>Removes the switch from the arguments passed to WebApplication.CreateBuilder (a bare "--key" would
    /// swallow the next argument as its value).</summary>
    public static GameServerCommandLine Parse(string[] args);
}

public static class HostExitCodes
{
    public const int Ok = 0;
    public const int InvalidConfiguration = 1;
    public const int ProvisioningFailed = 2;
}

public static class GameServerRunExtensions
{
    /// <summary>Serve: UseGameServer + RunAsync, mapping GameServerStartupException (from validation or a hosted
    /// service's start) to exit 1 with the message on stderr. Provision: see research R10.</summary>
    public static Task<int> RunGameServerAsync(this WebApplication app, HostMode mode);
}
```

```csharp
namespace GameServer.Platform.Persistence;

public interface IStoreProvisioner
{
    /// <summary>Creates what is missing, in order; reports each item; throws ProvisioningFailedException on the first failure.</summary>
    Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken);
}

public enum ProvisioningOutcome { Created, AlreadyPresent }

public sealed record ProvisioningItem(string Kind, string Name, ProvisioningOutcome Outcome, TimeSpan Elapsed);

public sealed class ProvisioningFailedException(string kind, string name, string cause, Exception? inner = null) : Exception
{
    public string Kind { get; }
    public string Name { get; }
}
```

Provision-mode flow (`RunGameServerAsync(HostMode.Provision)`): run only the checks provisioning depends on —
`registry.Validate(options, config: null)` (registration rules, collections, deletion treatments, indexes) and every
`IStartupCheck` (deployment identity, Couchbase settings). It does **not** call `UseGameServer`, `IStartupValidator` or
`app.StartAsync`, so GM SSO, IAP credentials, identity pepper and the data-protection master key are not required
(FR-031) and no hosted service (jobs, sweeper, key ring, Kestrel) runs. Errors → exit 1. No `IStoreProvisioner`
registered (in-memory / file store) → exit 1 "`--provision` needs GameServer:Store=Couchbase". Then
`ProvisionAsync` with a `CancellationTokenSource` of `ProvisioningTimeoutSeconds` (+ Ctrl+C) → success exit 0;
`ProvisioningFailedException`/timeout → exit 2. Output contract: [contracts/operations-cli.md](contracts/operations-cli.md).

`CouchbaseProvisioner` implements `IStoreProvisioner`; the startup path (`ProvisionOnStartup`) calls the same method with a
logging progress. Order (FR-029):

1. scope `g_<gameId>`;
2. collection `txn_meta` **first** (today it is appended last), then the other required collections in ordinal order;
3. wait until every collection answers a KV probe (R11), bounded by the timeout (manifest propagation);
4. indexes: read existing names once (`SELECT RAW name FROM system:indexes WHERE bucket_id=$b AND scope_id=$s`), then
   `CREATE INDEX … IF NOT EXISTS` for missing ones (framework, module, purge reference indexes — unchanged statements);
5. poll `system:indexes` every 2 s until every framework/module index is `online` (**[VERIFY]** whether `CREATE INDEX`
   without `defer_build` already blocks until built on 7.6; polling is correct either way), reporting the elapsed time;
6. final verification (R11) — must find nothing missing.

Idempotency (FR-030): existing items are reported `AlreadyPresent` and never altered; nothing is ever dropped (never
`txn_meta`, DAT-07). An existing index with the same name but another definition is reported present (definition drift
is not detected — documented). A re-run after a failure continues with what is still missing. Buckets are never created.

**Credential (FR-031).** The provisioning run uses the same keys `Couchbase:Username`/`Couchbase:Password`, set by the
pipeline step to an administrative identity (`Bucket Admin` on the bucket + `Query Manage Index`, documented in the
guide); the API deployment sets a data-only identity (`Data Reader`, `Data Writer`, `Query Select/Insert/Update/Delete`
on the scope). No second credential key: the provisioning run is a separate process with its own environment.

**Alternatives.** (a) A separate `tools/GameServer.Provision` executable — would need the game's module registration
duplicated (spec assumption rejects). (b) `ProvisionOnStartup=true` in Production with admin rights on every instance —
against least privilege (SEC-01). (c) Continue after an item failure to report all — first-failure stop is simpler to
reason about and a re-run reports the rest.

---

## R11 — Schema check when startup provisioning is off (FR-032)

**Decision.** When `ProvisionOnStartup` resolves to false, `CouchbaseProvisioner.EnsureInitializedAsync` runs
`VerifySchemaAsync` once (shared task, like provisioning) instead of returning at once. It probes every required
collection (including `txn_meta`) in parallel with a KV `GetAsync` of the non-existent key `_schema_probe` and a 3 s
timeout:

- `DocumentNotFoundException` → present;
- `CollectionNotFoundException`, or a timeout whose retry reasons include `CollectionNotFound` → missing
  (**[VERIFY]** the exception shape of SDK 3.10 for an unknown collection; an integration test drops a collection in a
  throw-away scope to pin it);
- anything else (cluster unreachable, authentication) → rethrown unchanged: the host fails to start exactly as it does
  today when the key ring cannot be read.

Missing collections → `GameServerStartupException`: `[couchbase] scope g_sample in bucket gs-live is missing collections
txn_meta, pa_wallet. Fix: run the provisioning command (<host> --provision) with an administrative credential
(DAT-07).` Because `KeyRingService.StartingAsync` awaits the initializer, the host stops **before Kestrel listens**;
`RunGameServerAsync` maps it to exit 1. "Does not serve traffic" therefore means the process exits non-zero before
binding; readiness is never reached (orchestrators restart it until ops provisions).

The probe needs only KV read rights on each collection — the runtime credential (spec assumption satisfied, no fallback
needed). Indexes are **not** checked at runtime: listing them needs the `Query System Catalog` role, which the data-only
identity does not have; a missing index fails the GM/job query that needs it with a logged error, and the provisioning
report is the source of truth.

**Alternatives.** (a) `bucket.Collections.GetAllScopesAsync()` — one call, but the REST manifest endpoint's required role
for a data-only user is uncertain. (b) Readiness 503 while missing — the key ring load would fail anyway before
readiness exists. (c) Detect on first use — late, per-request errors.

---

## R12 — Local run: launch profiles, scripts, LAN, listen warning, Unity default (FR-033..FR-039)

**Launch profiles** (`samples/SampleHost/Properties/launchSettings.json`, first = default for `dotnet run`):

- `local`: `commandName: Project`, `ASPNETCORE_ENVIRONMENT=Local`, `applicationUrl: http://localhost:5080`,
  `launchBrowser: true`, `launchUrl: gm/v1/ui` (IDE only; `dotnet run` ignores `launchBrowser`).
- `dev-couchbase`: `ASPNETCORE_ENVIRONMENT=Dev`, `applicationUrl: http://localhost:5080`, no browser. The password comes
  from the developer's **user** environment variable `Couchbase__Password` (inherited by IDE and `dotnet run`); nothing
  in the profile.

**Scripts** `tools/local/run-local.ps1` (Windows PowerShell 5.1 and PowerShell 7) and `tools/local/run-local.sh` (bash on
macOS/Linux, Git Bash on Windows). Contract in [contracts/operations-cli.md](contracts/operations-cli.md). Shape:

1. locate the SDK: `dotnet` on `PATH`, else `~/.dotnet10/dotnet(.exe)` (repo convention); `dotnet --version` run from the
   repository root must succeed (honours `global.json`), else exit 3 "SDK 10.0.403 not found";
2. refuse a busy port before building (PowerShell: try `TcpListener` on loopback; bash: `/dev/tcp` connect probe) →
   exit 4 naming the port and `-Port`;
3. `dotnet build samples/SampleHost/SampleHost.csproj -c Debug` (output visible) → exit 5 on failure;
4. start `dotnet run --no-build --launch-profile local -- --urls <url> [--FileStore:ResetOnStart=true]` **from
   `samples/SampleHost`** (working directory = content root; **[VERIFY]** `dotnet run`'s default working directory —
   setting it explicitly makes the question moot). PowerShell: `Start-Process -NoNewWindow -PassThru` (shares the
   console, logs stream); bash: a background poller subshell prints the URLs while `dotnet run` runs in the foreground
   (so the terminal's SIGINT reaches it directly — background jobs of non-interactive bash ignore SIGINT);
5. poll `GET <url>/health/ready` every 500 ms up to 60 s; host exit during the wait → exit 6 with its exit code (lock
   held, malformed file, port race — the host already printed the reason); timeout → print the last 20 lines of the newest
   file in `samples/SampleHost/logs/`, stop the host, exit 7;
6. print the URL block (player API base, health, GM UI, GM OpenAPI) — with `-Lan`, also the machine's IPv4 LAN
   addresses and a red warning block;
7. wait: PowerShell loops on `$p.HasExited` with 200 ms sleeps (interruptible by Ctrl+C), `finally` waits up to 35 s for
   the host (it received the same console Ctrl+C), then kills the process tree; bash `wait`s the foreground process.
   Exit code = host exit code (0 after a graceful stop).

Options: `-Port <n>` / `--port <n>` (default 5080); `-Reset` / `--reset` → `--FileStore:ResetOnStart=true` (the host
enforces the safety rules, R9); `-Lan` / `--lan` → `--urls http://0.0.0.0:<port>` (local flavor only — the script has no
flavor option, so LAN binding of other flavors through it is impossible).

**Listen warning (FR-038, SEC-03).** `RunGameServerAsync` registers on `ApplicationStarted`: when
`AllowsDevelopmentTools` and any address of `IServerAddressesFeature` is not loopback (`0.0.0.0`, `[::]`, `*`, `+`, a
non-loopback IP or host name), log one warning: development tools (test authentication, cheats, GM staff seed, GM
OpenAPI) are reachable from the network. `TestServer` has no addresses → no warning in tests.

**Unity sample default (FR-039).** `SampleGameClient.serverUrl` default and its serialized value in
`Assets/Scenes/SampleGameClient.unity`, `Editor/SmokeBuild.cs` and `sdk/unity/ci/smoke-build.ps1` → `http://localhost:5080/`
(edit the scene YAML line, no Unity Editor needed). README's GM URL → 5080.

**start-hosts.ps1.** Adds `ASPNETCORE_ENVIRONMENT=Dev` to its saved/restored variable list; everything else unchanged
(its explicit `GameServer__Store=Couchbase`, `Couchbase__*` and `--urls` still override the dev flavor file).

**Smoke test.** `SampleHostSmokeTests` runs the real local flavor: `UseEnvironment("Local")`,
`UseSetting("FileStore:Folder", <temp dir>)` (never the developer's `_database`), sweeper and jobs off; the test deletes
the temp folder. A second case restarts the factory on the same folder and reads the counter back (US1 persistence).

**Alternatives.** (a) Scripts run the dll from a copy like `start-hosts.ps1` — avoids locking `bin/` but the daily loop
expects `dotnet build` to need a stopped server anyway; `dotnet run --launch-profile local` keeps IDE and script on the
same profile (FR-034). (b) The script deletes the data folder itself — would need to parse layered JSON config in bash
without a JSON tool; the host already has the resolved value and the safety rules.

---

## R13 — OPS-09 on beta/uat (spec conflict 4)

**Decision.** Keep the stricter rule: Staging (beta, uat) gets no cheats, test authentication, GM staff seed, GM OpenAPI,
fake verifiers or fake identity providers. **No per-deployment switch in this slice.**

**Rationale.** OPS-09 *allows* cheats outside Production but does not require them; beta/uat run with real store sandbox
receipts and external testers, where test authentication by header (impersonate any player) and a committed seed password
are security incidents (SEC-03, SEC-16). Development (local, dev) covers QA needs today.

**Deferred extension point (owner may override).** If QA needs cheats on uat, add `GameServer:Staging:AllowCheatModule`
(Staging only, refused in Production): registers only `CheatModule`, behind a GM permission with audit, never test
authentication or the seed. Recorded in PCR platform as "not provided".
