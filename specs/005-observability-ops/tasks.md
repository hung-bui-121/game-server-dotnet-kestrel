---
description: "Task list for 005 observability-ops"
---

# Tasks: Observability + vận hành (P0 slice 005)

**Tests**: REQUIRED (ACC-17, ACC-25, ACC-28, ACC-41, ACC-51).

## Phase 1: Setup

- [X] T001 Pin Serilog/OpenTelemetry packages in `Directory.Packages.props`; create `src/GameServer.Platform.Observability` and `tests/GameServer.Observability.Tests`; add to solution

## Phase 2: Foundational

- [X] T002 [P] `IStoreHealthProbe` + default probe in `src/GameServer.Platform/Persistence/`; in-memory `Faults.Unavailable`
- [X] T003 [P] `command_results_total{operation, code}` in `PlatformMetrics` + runner; `http_errors_total{code}` in `ErrorResponseWriter`
- [X] T004 ForwardedHeaders options + validation + middleware; Production startup check that secrets (`GameServer:SecretKeys`) do not come from a JSON file (OPS-08) in `src/GameServer.Platform.Web/Hosting/`

## Phase 3: US1 — Log file

- [X] T005 [US1] `ObservabilityOptions`, `LogSinkMonitor` (SelfLog, drops, free bytes), `SensitiveDataRedactor` (enricher + destructuring policy)
- [X] T006 [US1] `AddObservability()` Serilog pipeline: async bounded → file (compact JSON, Day + size rolling, per-instance path)
- [X] T007 [US1] Tests: rolling + count limit, append on restart, sink error → metric + Degraded, secret scan after login/GM/command flows in `tests/GameServer.Observability.Tests/LoggingTests.cs`

## Phase 4: US2 — Trace + metrics

- [X] T008 [US2] OpenTelemetry tracing + metrics registration, OTLP exporter by config
- [X] T009 [US2] Tests with in-memory exporter: one trace HTTP → command, `gs.request_id` attribute, OBS-01 metrics present, no playerId tag in `TelemetryTests.cs`

## Phase 5: US3 — Health + shutdown

- [X] T010 [US3] Health checks `store`, `config`, `log`, `shutdown`; `/health/live`, `/health/ready` (Public, status only); allowlist
- [X] T011 [US3] `GET /gm/v1/health` (`ops.read`) in module gm; permission catalog + Administrator role
- [X] T012 [US3] Shutdown timeout option; tests: store down → ready 503 without details, live 200, GM detail; stopping → ready 503; command committing during stop completes in `HealthAndShutdownTests.cs`

## Phase 6: US4 — Proxy

- [X] T013 [US4] Tests: trusted proxy rewrites RemoteIp, untrusted does not; enabled without proxies fails startup in `ForwardedHeadersTests.cs`

## Phase 7: Polish

- [X] T014 [P] SampleHost uses `AddObservability()`; appsettings `Observability`; README run notes; PCR platform (logging/metrics/health) + gm (`ops.read`)
- [ ] T015 Full suite, architecture rule (Serilog/OTel only in Observability/Web), AgentMemories
