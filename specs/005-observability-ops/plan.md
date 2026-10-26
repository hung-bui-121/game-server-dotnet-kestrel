# Implementation Plan: Observability + vận hành (P0 slice 005)

**Branch**: `005-observability-ops` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

## Summary

Project mới `GameServer.Platform.Observability` (Serilog + OpenTelemetry + health checks) gắn vào host bằng
`gameServer.AddObservability()`. Platform.Web thêm ForwardedHeaders, metric lỗi theo code, graceful-shutdown readiness.
Platform thêm `command_results_total`. Module gm thêm `GET /gm/v1/health` (permission `ops.read`).

## Research / decisions

- **R1 Package (pin, stable)**: Serilog.AspNetCore 10.0.0, Serilog.Sinks.File 7.0.0, Serilog.Sinks.Async 2.1.0,
  Serilog.Formatting.Compact 3.0.0, OpenTelemetry.* 1.20.1 (Extensions.Hosting, Instrumentation.AspNetCore/Http/Runtime,
  Exporter.OpenTelemetryProtocol; Exporter.InMemory cho test). V10 (`retainedFileTimeLimit`): có trong Sinks.File ≥ 5.
- **R2 Serilog cùng Microsoft logging**: `AddSerilog(logger, dispose: true, writeToProviders: true)` — provider khác (test
  capture) vẫn nhận; dispose khi host dispose = flush (OPS-03).
- **R3 Queue có giới hạn**: `WriteTo.Async(bufferSize: 10000, blockWhenFull: false, monitor)` → inspector đếm drop.
- **R4 SelfLog** là static của Serilog → `LogSinkMonitor` dùng chung process; health `log` đọc lỗi gần nhất.
- **R5 Redaction**: enricher thay giá trị property có tên khớp danh sách nhạy cảm bằng `***` + destructuring policy cho
  object; framework vốn không log token/payload (kiểm bằng ACC-25 test).
- **R6 Health store**: `IStoreHealthProbe` mặc định = `GetAsync` một key không tồn tại trong `receipts` (timeout 2 s).
  In-memory store có cờ fault `Unavailable` cho test.
- **R7 Shutdown**: `IHostApplicationLifetime.ApplicationStopping` → check `shutdown` Unhealthy; runner đã tách token commit (PERF-02).
- **R8 ForwardedHeaders** đặt đầu pipeline trong `UseGameServer`; xoá mặc định loopback, chỉ dùng danh sách cấu hình.

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| IV | Queue log có giới hạn, không chặn request | PASS |
| VI | Test ACC-17/25/28/41/51 với store in-memory | PASS |
| VII | Log có cấu trúc, metrics, health, OTel, correlation | PASS |
| III | Health chi tiết chỉ qua GM permission | PASS |

## Structure

```text
src/GameServer.Platform.Observability/   # options, Serilog setup, redaction, sink monitor, OTel, health checks, endpoints
src/GameServer.Platform.Web/Hosting/      # ForwardedHeaders, error metric
src/GameServer.Platform/Diagnostics/      # command_results_total, IStoreHealthProbe
src/GameServer.Modules.Gm.Adapters/       # GET /gm/v1/health
tests/GameServer.Observability.Tests/
```
