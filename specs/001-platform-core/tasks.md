---
description: "Task list for 001 platform-core"
---

# Tasks: Platform Core (P0 slice 001)

**Input**: Design documents from `specs/001-platform-core/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: REQUIRED — spec FR-082/FR-083, Success Criteria và constitution VI yêu cầu unit, web, arch
và integration test (integration gated bởi `COUCHBASE_CONNSTR`).

**Build/test command**: `& "$env:USERPROFILE\.dotnet10\dotnet.exe" test GameServer.slnx -c Release`

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: US1..US8 theo spec.md

---

## Phase 1: Setup (Shared Infrastructure)

- [ ] T001 Create `global.json` (sdk 10.0.403, rollForward latestPatch), `GameServer.slnx`, folder layout per plan.md (`src/`, `modules/`, `samples/`, `templates/`, `tests/`, `docs/`, `proto/`)
- [ ] T002 Create `Directory.Build.props` (net10.0, Nullable enable, ImplicitUsings enable, LangVersion latest, TreatWarningsAsErrors true, AnalysisLevel latest-recommended for `src/` and `modules/`, deterministic) and `Directory.Packages.props` with pinned versions from research.md R2 (CouchbaseNetClient 3.10.0, Google.Protobuf 3.36.2, Grpc.Tools 2.84.0, xunit.v3 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.11.0, Shouldly 4.3.0, NetArchTest.Rules 1.3.2, Microsoft.AspNetCore.Mvc.Testing 10.0.14, Microsoft.CodeAnalysis.BannedApiAnalyzers latest stable)
- [ ] T003 [P] Create `BannedSymbols.txt` banning `Task.Wait*`, `Task`1.Result`, `TaskAwaiter.GetResult`, `DateTime.UtcNow/Now`, `DateTimeOffset.UtcNow/Now` and wire it via `Directory.Build.props` for `src/` and `modules/` projects (PERF-01, TIME-01)
- [ ] T004 [P] Create projects: `src/GameServer.Core`, `src/GameServer.Platform`, `src/GameServer.Platform.Couchbase`, `src/GameServer.Platform.Testing`, `src/GameServer.Platform.Web` (Microsoft.NET.Sdk.Web library: FrameworkReference Microsoft.AspNetCore.App), `src/GameServer.Modules.Identity`, `modules/Sample.Counter/Sample.Counter`, `modules/Sample.Counter/Sample.Counter.Adapters`, `samples/SampleHost` with references per plan.md (Core ← Platform ← Platform.Couchbase/Testing/Web; module business project references only Core)
- [ ] T005 [P] Create test projects `tests/GameServer.Platform.Tests`, `tests/GameServer.Platform.Web.Tests`, `tests/GameServer.Architecture.Tests`, `tests/Sample.Counter.Tests`, `tests/GameServer.Platform.Couchbase.IntegrationTests` (xunit.v3, Shouldly) and add all projects to `GameServer.slnx`
- [ ] T006 [P] Copy `specs/001-platform-core/contracts/core.proto` to `proto/gameserver/core/v1/core.proto` and configure Protobuf codegen (`<Protobuf Include=... GrpcServices="None" />`) in `src/GameServer.Platform.Web/GameServer.Platform.Web.csproj`; copy `sample_counter.proto` to `modules/Sample.Counter/Sample.Counter.Adapters/Protos/sample_counter.proto` with codegen
- [ ] T007 Verify `dotnet build GameServer.slnx -c Release` succeeds with empty projects

---

## Phase 2: Foundational (Blocking Prerequisites)

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Core contracts (`src/GameServer.Core`)

- [ ] T008 [P] Implement `IClock` + `SystemClock` (only place allowed to read system time, `#pragma` suppress banned API with justification) in `src/GameServer.Core/Time/IClock.cs`
- [ ] T009 [P] Implement `ErrorCode` record (`Number`, `Name` UPPER_SNAKE, `HttpStatus`, `Retryable`, `SafeMessage`), `ErrorCodeSet` (range start/end + codes) and `CoreErrorCodes` (1–999: `OK`=0 special, `BAD_REQUEST`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `PAYLOAD_TOO_LARGE`, `UNSUPPORTED_MEDIA_TYPE`, `RATE_LIMITED`, `REQUEST_TIMEOUT`, `MAINTENANCE`, `UPDATE_REQUIRED`, `IDEMPOTENCY_PAYLOAD_MISMATCH`, `CONCURRENT_UPDATE`, `COMMIT_UNKNOWN_RETRY`, `AGGREGATE_TOO_LARGE`, `INTERNAL`) with HTTP mapping from contracts/http-routes.md in `src/GameServer.Core/Errors/`
- [ ] T010 [P] Implement `ActorContext` (`ActorId`, `ActorType` player/gm/system, `PlayerId`, `GameId`, `EnvironmentId`, `Permissions`, `CorrelationId`, `Profile`) and `SecurityProfile` enum (`Public`, `Player`, `Gm`, `Dev`) in `src/GameServer.Core/Actors/`
- [ ] T011 [P] Implement `PlayerAggregateDescriptor` (non-generic base + `PlayerAggregateDescriptor<TState>`: `AggregateId` matching `[a-z][a-z0-9_]{1,31}`, `SchemaVersion` ≥ 1, `ClientVisible`, `CreateDefault`, `Upgraders` (`IAggregateUpgrader { int FromVersion; JsonObject Upgrade(JsonObject state); }`), `WarnBytes = 32768`, `HardBytes = 65536`) in `src/GameServer.Core/Aggregates/`
- [ ] T012 [P] Implement `CommandDescriptor` (`Operation` `<module>.<command>`, `Touches` 1..4, `Reads`, `MaxClaimMarkers` 0..2, `Profile`, `Permission`), `ICommandHandler<TRequest,TResult>`, `CommandOutcome<TResult>` (`Ok(result)` / `Reject(ErrorCode)`), `ICommandContext` (Actor, Clock, ConfigVersion, Config, `Get<TState>`, `Set<TState>`, `TryClaimAsync(collection, key)`, `AddOutbox(kind, payload)`) and `IResultCodec<TResult>` (result ↔ bytes, ≤ 512 B) in `src/GameServer.Core/Commands/`
- [ ] T013 [P] Implement `ModuleDescriptor` (Name, Version, DependsOn, RequiredConfigFiles, Permissions, Collections (+index spec), Aggregates, Commands, ErrorCodes, OutboxConsumers), `IGameModule` (`Describe()`, `ConfigureServices(IServiceCollection, ModuleContext)`), `ModuleContext` (GameId, EnvironmentId, DeploymentEnvironment) in `src/GameServer.Core/Modules/`
- [ ] T014 [P] Implement `IGameConfig` (`ConfigVersion`, `Label`, `GetFile<T>(name)`, `TryGetFileJson(name)`, `ClientVisibleFiles`) in `src/GameServer.Core/Config/IGameConfig.cs`

### Platform runtime (`src/GameServer.Platform`)

- [ ] T015 Implement deployment options `GameServerOptions` (`GameId`, `EnvironmentId`, `DeploymentEnvironment` Development/Staging/Production, `ConfigPath`, `Store` InMemory/Couchbase, transaction/outbox/limit settings with defaults from plan Constraints) in `src/GameServer.Platform/GameServerOptions.cs`
- [ ] T016 Implement persistence abstraction per contracts/module-api.md: `DocumentKey` (Collection, Key), `StoredDocument` (Key, `JsonObject Content`, opaque `Version`), `ITransactionalDocumentStore`, `ITransactionContext`, `TransactionRunOptions` (Expiration 10 s), exceptions `TransactionCommitAmbiguousException`, `TransactionConflictExpiredException`, `TransactionFailedException`, `DocumentExistsException` in `src/GameServer.Platform/Persistence/`
- [ ] T017 Implement `ModuleRegistry` collecting descriptors, aggregates, commands, error codes, outbox consumers, with `Validate()` returning all violations (duplicate command/error name/number/overlapping ranges, missing dependency, aggregate count ≤ 16, Touches 1..4 and known, Reads known, MaxClaimMarkers ≤ 2, `profile` aggregate required, invalid ids) in `src/GameServer.Platform/Registry/ModuleRegistry.cs`
- [ ] T018 Implement `ErrorCodeCatalog` (lookup by name/number, includes core + module codes) in `src/GameServer.Platform/Registry/ErrorCodeCatalog.cs`
- [ ] T019 Implement `FileGameConfigLoader` per data-model.md "Config snapshot": read `config/base/manifest.json` `{label, files[], clientVisible[]}`, overlay `config/env/<env>/` (manifest + whole-file replace), `configVersion = "{label}+{hex(sha256(concat(sorted(name + '\n' + bytes))))[0..12]}"`, missing/unparseable file or missing required module file → `GameServerStartupException` (CFG-01, ARC-06) in `src/GameServer.Platform/Config/FileGameConfigLoader.cs`
- [ ] T020 Implement `AggregateDocumentSerializer`: envelope `{gameId, environmentId, playerId, aggregateId, schemaVersion, rev, createdAtUtc, updatedAtUtc, state}`, default when missing (`rev = 0`), lazy upgrade chain on `JsonObject` with counter callback, size measurement of serialized bytes in `src/GameServer.Platform/Aggregates/AggregateDocumentSerializer.cs`
- [ ] T021 Implement `PlatformMetrics` (`Meter "GameServer.Platform"`: `aggregate_size_bytes` histogram tag aggregateId, `aggregate_upgrades_total`, `txn_attempts_total`, `txn_retries_total`, `txn_ambiguous_total`, `txn_expired_total`, `txn_duration_seconds`, `receipt_size_bytes`, `outbox_relay_inline_failures_total`, `outbox_pending_age_seconds`, `config_version_info`) and `ActivitySource "GameServer.Platform"` in `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs`
- [ ] T022 Implement `GameServerStartupException` (list of violations, message: module, item, fix) in `src/GameServer.Platform/GameServerStartupException.cs`

### In-memory store (`src/GameServer.Platform.Testing`)

- [ ] T023 Implement `InMemoryDocumentStore : ITransactionalDocumentStore` per research.md R15 (versioned docs, buffered txn writes, validate read/write versions at commit under lock, retry lambda until Expiration then `TransactionConflictExpiredException`, non-txn reads see committed only, `InsertIfAbsentAsync`, `MarkOutboxRelayedAsync`, `QueryPendingOutboxAsync`) in `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs`
- [ ] T024 Implement `StoreFaultInjector` (`FailBeforeCommit`, `AmbiguousBeforeCommit`, `AmbiguousAfterCommit`, `ConflictOnce`, `DelayCommit`, one-shot or N-times) used by `InMemoryDocumentStore` in `src/GameServer.Platform.Testing/StoreFaultInjector.cs`
- [ ] T025 [P] Implement `FakeClock : IClock` (settable, `Advance`) in `src/GameServer.Platform.Testing/FakeClock.cs`
- [ ] T026 [P] Unit tests for `InMemoryDocumentStore` (atomic commit, conflict retry, insert-exists, uncommitted invisibility, each fault) in `tests/GameServer.Platform.Tests/Persistence/InMemoryDocumentStoreTests.cs`

### Web foundation (`src/GameServer.Platform.Web`)

- [ ] T027 Implement `GameServerBuilder` + `AddGameServer(this WebApplicationBuilder, Action<GameServerBuilderOptions>)` (`UseInMemoryStore()`, `UseCouchbase()`), `AddModule<T>(Action<ModuleRegistrationOptions>? configure)` with `ServiceOnly()`, `ConfigureExtensions(Action<ExtensionRegistry>)` executed after all framework modules, options binding from `GameServer` config section in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`
- [ ] T028 Implement `IPlayerApiModule`, `IGmApiModule`, `IPlayerRouteBuilder`/`IGmRouteBuilder` wrappers that force `/api/v{n}/` (Player), `/gm/v{n}/` (GM) prefixes and attach `SecurityProfileMetadata` + optional `PermissionMetadata` in `src/GameServer.Platform.Web/Routing/`
- [ ] T029 Implement `ErrorResponseWriter` (Player → `ErrorEnvelope` protobuf `application/x-protobuf`; GM/Public → Problem Details JSON with `code`, `codeNumber`, `requestId`, `retryable`, `retryAfterMs`; chooses by endpoint profile metadata or `/api/` path prefix when unmatched; sets `Retry-After`; never includes exception detail) in `src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs`
- [ ] T030 Implement `CorrelationMiddleware` (`X-Request-Id` read/validate UUID else generate, echo header, `Activity` tag, logging scope) and `ExceptionMiddleware` (maps platform exceptions to codes, unknown → 500 `INTERNAL`) in `src/GameServer.Platform.Web/Middleware/`
- [ ] T031 Implement `ProtobufBinding` helpers: read body with max size + `CodedInputStream` size/recursion limit, reject non `application/x-protobuf` with 415, write message with `serverTimeUtc` in `src/GameServer.Platform.Web/Protobuf/ProtobufBinding.cs`
- [ ] T032 Implement test authentication scheme `GameServerTest` (headers `X-Test-Player`, `X-Test-Gm`, `X-Test-Gm-Permissions`) building `ActorContext` with GameId/EnvironmentId from options only; registration API `UseTestAuthentication()` in `src/GameServer.Platform.Web/Auth/TestAuthenticationHandler.cs`
- [ ] T033 Implement `ActorContextAccessor` (from authenticated principal + options; ignores payload identity, API-12) in `src/GameServer.Platform.Web/Auth/ActorContextAccessor.cs`
- [ ] T034 Implement `UseGameServer(this WebApplication)` wiring middleware order per research.md R11 (exception → correlation → gate slot → authentication → profile enforcement → authorization → request timeouts → rate limiter) and mapping module endpoints in `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`
- [ ] T035 [P] Implement `GameServer.Modules.Identity` `IdentityModule` registering required aggregate `profile` (state: `displayName`, `sessionEpoch`, `createdAtUtc`; schemaVersion 1, client-visible) in `src/GameServer.Modules.Identity/IdentityModule.cs`
- [ ] T036 Create `samples/SampleHost/Program.cs` + `appsettings.json` (`GameServer:GameId=sample`, `EnvironmentId=dev`, `DeploymentEnvironment=Development`, `Store=InMemory`) + `config/base/manifest.json` (label `sample-1`) registering Identity module, test auth
- [ ] T037 [P] Create `tests/GameServer.Platform.Web.Tests/Infrastructure/GameServerFactory.cs` (`WebApplicationFactory<Program>` with in-memory store, fake clock, fault injector access, helpers to POST protobuf with headers)

**Checkpoint**: Foundation ready

---

## Phase 3: User Story 1 - Command commit đúng một lần (Priority: P1) 🎯 MVP

**Goal**: Command có `requestId` commit nguyên tử aggregate + receipt, replay trả cùng kết quả.

**Independent Test**: `Sample.Counter` Increment gửi 3 lần tuần tự/đồng thời → 1 receipt, value tăng 1 lần (quickstart #1–#4).

### Tests for User Story 1

- [ ] T038 [P] [US1] Unit tests `CommandRunnerTests`: first commit (`rev 0→1`, SectionUpdate full), replay same hash (`replayed = true`, same receiptId/newRev/result bytes), payload mismatch 409, two actors same requestId independent, ambiguous-after-commit → `COMMIT_UNKNOWN_RETRY` then retry returns committed result, ambiguous-before-commit retry executes once, concurrent same requestId (Task.WhenAll ×10) one effect, replay window expiry (FakeClock +25h) treated as new, business reject → no receipt/no write, handler exception → rollback, write to undeclared aggregate → error, unchanged aggregates not in updates and rev unchanged, receipt > 4 KiB / resultCompact > 512 B rejected in `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs`
- [ ] T039 [P] [US1] Web tests for `/api/v1/counter/increment` (ACC-04, ACC-05, ACC-07, ACC-44 single-process variant, missing/invalid `X-Request-Id` → 400) in `tests/GameServer.Platform.Web.Tests/CommandPipelineTests.cs`

### Implementation for User Story 1

- [ ] T040 [US1] Implement `ReceiptDocument` model + JSON mapping per data-model.md Receipt (fields, `receiptId` = base64url(SHA-256(actorId|operation|requestId))[0..32], `bodyHash` hex SHA-256, ≤ 4 KiB, `resultCompact` ≤ 512 B, `outboxPending`) in `src/GameServer.Platform/Commands/ReceiptDocument.cs`
- [ ] T041 [US1] Implement `CommandContext : ICommandContext` (loaded aggregate states, change tracking by serialized comparison, claims list ≤ MaxClaimMarkers, outbox entries with `entryId = <receiptId>:<index>`) in `src/GameServer.Platform/Commands/CommandContext.cs`
- [ ] T042 [US1] Implement `CommandRunner : ICommandRunner` per data-model.md state transitions: pre-txn receipt lookup (non-txn get), per-attempt `GetManyAsync(touches ∪ reads ∪ receipt)`, in-txn receipt race check, handler, size checks (`AGGREGATE_TOO_LARGE` reject), Replace/Insert changed aggregates with `rev+1`/`updatedAtUtc`, claim markers (GetMany then Insert, research R6), Insert receipt, commit without request token (PERF-02), exception mapping (ambiguous → COMMIT_UNKNOWN_RETRY, expired → CONCURRENT_UPDATE), metrics in `src/GameServer.Platform/Commands/CommandRunner.cs`
- [ ] T043 [US1] Implement `PerPlayerCommandLimiter` (PartitionedRateLimiter concurrency 1, queue 2 by `playerId`; exceed → `RATE_LIMITED`) in `src/GameServer.Platform/Commands/PerPlayerCommandLimiter.cs`
- [ ] T044 [US1] Implement `CommandEndpointFactory` in Platform.Web: `routes.MapCommand<TReq,TResult,TWireReq,TWireResult>(descriptor, mapRequest, mapResult)` → reads raw body (64 KiB), hashes, requires `X-Request-Id`, decodes, maps to domain request (ARC-02), runs `ICommandRunner`, builds `CommandResponse` (updates via aggregate wire mappers, `result` bytes) in `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`
- [ ] T045 [US1] Implement `IAggregateWireMapper<TState>` registry (state → protobuf `IMessage` bytes) in `src/GameServer.Platform.Web/Aggregates/AggregateWireMapperRegistry.cs`
- [ ] T046 [P] [US1] Implement `Sample.Counter` business project: `CounterState {Value, Increments}`, descriptor (aggregate `counter`, schemaVersion 2 with upgrader v1→v2 adding `increments`, client-visible), `IncrementCommand {By, Note}`, `IncrementResult {Value}`, `ICounterLimitPolicy` + `DefaultCounterLimitPolicy` (max 10 per increment, `COUNTER_LIMIT_EXCEEDED` code 10001), `IncrementHandler`, error range 10000–10099 in `modules/Sample.Counter/Sample.Counter/`
- [ ] T047 [US1] Implement `Sample.Counter.Adapters`: `CounterModule : IGameModule, IPlayerApiModule` mapping `POST /api/v1/counter/increment` and `POST /api/v1/counter/get`, wire mapper `CounterState`, result codec in `modules/Sample.Counter/Sample.Counter.Adapters/`
- [ ] T048 [US1] Register `CounterModule` in `samples/SampleHost/Program.cs` and make US1 tests pass

**Checkpoint**: MVP — idempotent commit end-to-end on in-memory store

---

## Phase 4: User Story 2 - Thêm module không sửa Core (Priority: P1)

**Goal**: Module đăng ký một lệnh, thay policy/handler, chế độ service-only.

**Independent Test**: thay `ICounterLimitPolicy`, service-only, ReplaceHandler (quickstart #6).

- [ ] T049 [P] [US2] Tests: replace policy (limit 100 works), `ServiceOnly()` maps no counter endpoint but `ICounterService` resolvable, `ReplaceHandler("counter.increment", …)` still goes through idempotency/auth, duplicate handler registration fails, Core assembly has no reference to Sample.* in `tests/Sample.Counter.Tests/ExtensionTests.cs`
- [ ] T050 [US2] Implement `ExtensionRegistry` (`ReplacePolicy<TService,TImpl>()`, `ReplaceHandler(operation, factory)`, applied after module registration in explicit order) in `src/GameServer.Platform.Web/Hosting/ExtensionRegistry.cs`
- [ ] T051 [US2] Implement `ICounterService` (read counter via snapshot reader) usable by other modules in `modules/Sample.Counter/Sample.Counter/ICounterService.cs` and adapter implementation in `modules/Sample.Counter/Sample.Counter.Adapters/CounterService.cs`
- [ ] T052 [P] [US2] Create module template `templates/module/Template.Module/` and `templates/module/Template.Module.Adapters/` (csproj, descriptor, aggregate, command, handler, endpoint skeleton, README) (FR-081)
- [ ] T053 [P] [US2] Write `docs/guides/module-authoring.md` (register, aggregate, command, policy override, service-only, error range, outbox consumer)

**Checkpoint**: ACC-13 satisfied

---

## Phase 5: User Story 3 - Cấu hình sai bị chặn khi khởi động (Priority: P1)

**Goal**: Startup validator fail-fast.

**Independent Test**: host thử với từng cấu hình sai → exception liệt kê vi phạm.

- [ ] T054 [P] [US3] Tests: endpoint without profile, two profiles, route without `/v{n}/`, duplicate route+method, duplicate command, duplicate error name/number/range overlap, Public route outside allowlist, missing dependency, missing required config file, missing GameId/EnvironmentId, > 4 Touches, > 16 aggregates, unknown aggregate in Touches, missing `profile`, `Dev` profile in Production, test auth in Production, deny-by-default when validator disabled in `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs`
- [ ] T055 [US3] Implement `EndpointSecurityValidator` (inspects `EndpointDataSource`: exactly one profile, version prefix, duplicates route+method, Public allowlist from options `PublicEndpointAllowlist` default `[/v1/time]`, `Dev` profile and test auth forbidden in Production) in `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs`
- [ ] T056 [US3] Implement `GameServerStartupValidator` running registry validation + config validation + endpoint validation, throwing `GameServerStartupException` before serving; hook into `UseGameServer` (and `IHostedService` start) in `src/GameServer.Platform.Web/Validation/GameServerStartupValidator.cs`
- [ ] T057 [US3] Implement `ProfileEnforcementMiddleware` (endpoint without profile → 403 `FORBIDDEN`; profile mismatch with principal → 401/403) in `src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs`
- [ ] T058 [US3] Ensure `SampleHost` exits non-zero with readable message on `GameServerStartupException` in `samples/SampleHost/Program.cs`

---

## Phase 6: User Story 4 - Snapshot và `rev` (Priority: P2)

**Goal**: Snapshot aggregate client-visible; SectionUpdate chỉ aggregate đổi.

**Independent Test**: quickstart #12.

- [ ] T059 [P] [US4] Tests: new player snapshot defaults `rev = 0`, non-client-visible excluded, filtered `[counter]`, unknown section → 400, after increment rev = 1, receipt lookup by owner OK / other actor 404 in `tests/GameServer.Platform.Web.Tests/SnapshotTests.cs`
- [ ] T060 [US4] Implement `PlayerSnapshotReader` (parallel non-txn `GetManyAsync` of client-visible aggregates, lazy upgrade, default for missing) in `src/GameServer.Platform/Aggregates/PlayerSnapshotReader.cs`
- [ ] T061 [US4] Implement `PlayerCoreEndpoints` (`POST /api/v1/player/snapshot`, `GET /api/v1/player/receipts/{receiptId}` owner-only → `ReceiptView`) in `src/GameServer.Platform.Web/Endpoints/PlayerCoreEndpoints.cs`

---

## Phase 7: User Story 5 - Lỗi nhất quán, có giới hạn (Priority: P2)

**Goal**: ErrorEnvelope/Problem Details, limits, `/v1/time`.

**Independent Test**: quickstart #7, #8.

- [ ] T062 [P] [US5] Tests: 413 (65 KiB body), 415 (JSON on Player), 401 (no auth), 429 (command limiter exhausted with permit 1 test config, `Retry-After`), 404 unknown Player route → ErrorEnvelope, GM error → Problem Details, every Player response has `server_time_utc_ms`, `/v1/time`, business reject → 200 + code, unhandled exception → 500 without stack trace, payload `playerId` ignored in `tests/GameServer.Platform.Web.Tests/ErrorContractTests.cs`
- [ ] T063 [US5] Implement limits: Kestrel `MaxRequestBodySize` 1 MiB, per-endpoint body size feature (Player 64 KiB, GM 1 MiB), `AddRequestTimeouts` policies `player` 10 s / `gm` 30 s with ErrorEnvelope/Problem Details on timeout in `src/GameServer.Platform.Web/Limits/RequestLimits.cs`
- [ ] T064 [US5] Implement `AddRateLimiter` concurrency policies `command` (200, queue 400, OldestFirst) and `gm` (50, queue 100) with `OnRejected` → `RATE_LIMITED` + `Retry-After`, values from options in `src/GameServer.Platform.Web/Limits/RateLimiting.cs`
- [ ] T065 [US5] Implement `GateMiddleware` slot (interface `IGateEvaluator` default allow-all; maintenance → 503 `MAINTENANCE`, version → 426 `UPDATE_REQUIRED`) placed before authentication in `src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`
- [ ] T066 [US5] Implement `TimeEndpoint` `GET /v1/time` (Public) in `src/GameServer.Platform.Web/Endpoints/TimeEndpoint.cs`
- [ ] T067 [US5] Implement GM example endpoint `GET /gm/v1/counter/{playerId}` (permission `counter.read`, JSON) in `modules/Sample.Counter/Sample.Counter.Adapters/CounterGmApi.cs` with `PermissionAuthorization` check in `src/GameServer.Platform.Web/Auth/PermissionAuthorization.cs`

---

## Phase 8: User Story 6 - Kích thước và nâng cấp schema (Priority: P2)

**Goal**: 32/64 KiB budget; lazy upgrade.

**Independent Test**: quickstart #9, #10.

- [ ] T068 [P] [US6] Tests: state > 32 KiB commit OK + histogram recorded; > 64 KiB → `AGGREGATE_TOO_LARGE`, no aggregate/receipt written (two-aggregate command: neither changes); v1 document read via snapshot returns v2 shape, DB unchanged until next command, upgrade counter incremented in `tests/GameServer.Platform.Tests/Aggregates/AggregateBudgetAndUpgradeTests.cs`
- [ ] T069 [US6] Add test-only module `SizeProbe` (aggregate `probe` + `sizeprobe.grow` command touching `probe` and `counter`) in `tests/GameServer.Platform.Tests/TestModules/SizeProbeModule.cs`
- [ ] T070 [US6] Wire warn/hard checks + upgrade counter into `CommandRunner` and `PlayerSnapshotReader` (if not already) in `src/GameServer.Platform/Aggregates/AggregateBudget.cs`

---

## Phase 9: User Story 7 - Outbox (Priority: P2)

**Goal**: relay inline + sweeper, idempotent.

**Independent Test**: quickstart #11.

- [ ] T071 [P] [US7] Tests: 2 entries relayed + receipt not pending; inline relay disabled → sweeper relays within 2 cycles (FakeClock + manual tick); relay + sweeper same entry → one target doc; rejected/rolled-back command → no entries; consumer throwing → command still OK, metric incremented in `tests/GameServer.Platform.Tests/Outbox/OutboxTests.cs`
- [ ] T072 [US7] Implement `IOutboxConsumer` (`Kind`, `HandleAsync`) + `ProjectionOutboxConsumer` (InsertIfAbsent `<kind>::<entryId>` into configured collection) in `src/GameServer.Platform/Outbox/`
- [ ] T073 [US7] Implement `OutboxRelay` (dispatch by kind, mark relayed when all succeed, swallow+metric on failure) and call it after commit in `CommandRunner` in `src/GameServer.Platform/Outbox/OutboxRelay.cs`
- [ ] T074 [US7] Implement `OutboxSweeper : BackgroundService` (period 30 s, pending older than 10 s, batch 100, `outbox_pending_age_seconds`) with testable `SweepOnceAsync` in `src/GameServer.Platform/Outbox/OutboxSweeper.cs`
- [ ] T075 [US7] Add `counter_log` outbox entry to `IncrementHandler` (payload `{by, note, newValue}`, note ≤ 64 chars) and register projection consumer to collection `counter_log` in `modules/Sample.Counter/`

---

## Phase 10: User Story 8 - Couchbase adapter + TST-04 (Priority: P3)

**Goal**: store thật + test thực nghiệm, gated.

**Independent Test**: không có env → Skipped với lý do; có env → chạy.

- [ ] T076 [US8] Implement `CouchbaseConnection` (singleton `ICluster` with `TransactionsConfig`: `MetadataCollection = g_<gameId>.txn_meta`, `ExpirationTime` 10 s, `DurabilityLevel.Majority`, `KvDurableTimeout` 5 s, cleanup window 60 s; bucket/scope/collection cache) in `src/GameServer.Platform.Couchbase/CouchbaseConnection.cs`
- [ ] T077 [US8] Implement `CouchbaseDocumentStore : ITransactionalDocumentStore` (txn via `cluster.Transactions.RunAsync`, map SDK exceptions to platform exceptions, non-txn `GetAsync`/parallel get, `InsertIfAbsentAsync`, `MarkOutboxRelayedAsync` via `MutateIn` on committed receipt, `QueryPendingOutboxAsync` N1QL with `request_plus`) in `src/GameServer.Platform.Couchbase/CouchbaseDocumentStore.cs`
- [ ] T078 [US8] Implement `CouchbaseGetMultiAdapter` (`ctx.GetMulti` with latency-priority mode, fallback sequential `GetOptionalAsync`) in `src/GameServer.Platform.Couchbase/CouchbaseGetMultiAdapter.cs`
- [ ] T079 [US8] Implement `CouchbaseProvisioner` (scope `g_<gameId>`, collections from descriptors: `pa_<id>`, `receipts`, `txn_meta`, module collections; primary-less partial index `receipts(outboxPending) WHERE outboxPending = true`; runs at startup only in Development, CLI flag `--provision` otherwise) in `src/GameServer.Platform.Couchbase/CouchbaseProvisioner.cs`
- [ ] T080 [US8] Wire `UseCouchbase()` in `GameServerBuilder` (options `Couchbase:ConnectionString/Username/Password/Bucket`, secrets from env) in `src/GameServer.Platform.Couchbase/CouchbaseServiceCollectionExtensions.cs`
- [ ] T081 [P] [US8] Implement `CouchbaseFact`-style gating helper (`Assert.Skip` when `COUCHBASE_CONNSTR` unset) and fixture provisioning a unique scope per run in `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/CouchbaseFixture.cs`
- [ ] T082 [US8] Implement shared `StoreConformanceTests` (abstract) in `tests/GameServer.Platform.Tests/Persistence/StoreConformanceTests.cs`, run for in-memory in `tests/GameServer.Platform.Tests/Persistence/InMemoryStoreConformanceTests.cs` and Couchbase in `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseStoreConformanceTests.cs`
- [ ] T083 [US8] Implement TST-04 tests (V15-T1, V15-T2, V15-T3, V23-T1, V17-T1, V17-T2, V4-T1, V16-T1) printing measured results in `tests/GameServer.Platform.Couchbase.IntegrationTests/Tst04TransactionExperiments.cs`

---

## Phase 11: Polish & Cross-Cutting Concerns

- [ ] T084 [P] Architecture tests (ARC-01 Core/module business projects don't depend on AspNetCore/Couchbase/Google.Protobuf/Serilog; only Platform.Couchbase depends on `Couchbase`; only `GameServer.Platform` uses `ITransactionalDocumentStore` write methods; no `ControllerBase` subclasses; module business projects don't reference Platform.*) in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs`
- [ ] T085 [P] Write `docs/pcr/platform.md` (limits, timeouts, replay window, durability, transaction boundary, error catalog core table, public allowlist, receipt rules incl. research R5, TST-04 results placeholder "not yet run — no Couchbase") and `docs/pcr/sample-counter.md`
- [ ] T086 [P] Write `docs/adr/0001-runner-store-abstraction.md`
- [ ] T087 Update root `README.md` (build with SDK 10.0.403, test, run SampleHost, links)
- [ ] T088 Run full build + tests in Release, fix warnings; run quickstart scenarios via tests; record results in `specs/001-platform-core/quickstart.md` "Validation log"

---

## Dependencies & Execution Order

- Setup (T001–T007) → Foundational (T008–T037) → US1 (T038–T048) → US2/US3/US4/US5/US6/US7 (each depends on US1's runner + counter module) → US8 (needs store abstraction + conformance tests) → Polish.
- Within US1: T040–T041 → T042 → T043–T045 → T046–T047 → T048; tests T038–T039 written first.
- US6 depends on T042 hooks; US7 modifies `CommandRunner` (T073) — do after US6 to avoid edit conflicts.

## Parallel Opportunities

- T003–T006 in parallel; T008–T014 in parallel (separate files); T025, T026, T035, T037 in parallel.
- After US1: US2 (T049–T053), US3 (T054–T058), US4 (T059–T061), US5 (T062–T067) touch mostly disjoint files.
- Polish T084–T086 in parallel.

## Implementation Strategy

1. MVP = Phase 1–3 (idempotent command on in-memory store).
2. Add US2 + US3 (extension + fail-fast) → framework usable by game devs.
3. Add US4–US7 → full P0 platform contract.
4. US8 → Couchbase adapter ready for CI; run when infrastructure available.
5. Commit after each phase (`feat:` / `test:` / `docs:`), no Claude signature.
