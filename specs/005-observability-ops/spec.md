# Feature Specification: Observability + vận hành (P0 slice 005)

**Feature Branch**: `005-observability-ops`

**Created**: 2026-10-26

**Status**: Draft

**Input**: "005 observability/ops — log file (rolling, per instance, async có giới hạn, che dữ liệu nhạy cảm), metrics + trace
OpenTelemetry, health live/ready, graceful shutdown, ForwardedHeaders. Nguồn: LOG-01..06, OBS-01..04, OPS-01/02/03/08/10,
ACC-17/25/28/41/51."

## Scope note

- Couchbase SDK tracing/metrics (OBS-04 [VERIFY] V2) và `txn_cleanup_lost_total` phụ thuộc cluster thật: P0 chỉ nối
  `ActivitySource`/`Meter` của framework; tích hợp SDK ghi lại sau khi chạy được TST-04.
- Prometheus exporter của OpenTelemetry .NET còn prerelease → P0 xuất **OTLP** (collector có thể xuất Prometheus).
- Data Protection key ring chia sẻ (OPS-04), runbook đầy đủ (OPS-07), rotation secret (OPS-08 phần P0.5): P0.5.
- Linux container/service guide (OPS-02 SHOULD): chỉ ghi chú cấu hình trong quickstart; ForwardedHeaders làm ở slice này.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Operator đọc log của từng instance (Priority: P1)

Mỗi instance ghi log JSON gọn vào file riêng (tên chứa instance id), cuộn theo ngày và kích thước, giữ số file giới hạn.
Log có thời gian UTC, level, nguồn, instance, request id và trace id. Token, mật khẩu, secret, pepper không bao giờ xuất
hiện. Khi không ghi được file hoặc đĩa gần đầy, metric tăng và readiness báo degraded; ledger/audit không bị ảnh hưởng.

**Acceptance Scenarios**:

1. **Given** giới hạn file nhỏ, **When** ghi nhiều log, **Then** có nhiều file cuộn, số file ≤ giới hạn (LOG-02, ACC-17).
2. **Given** instance khởi động lại cùng thư mục, **When** ghi tiếp, **Then** append vào file hiện tại, không ghi đè (LOG-01).
3. **Given** chạy guest login, GM login, command, **When** quét file log, **Then** không có token/password/secret (ACC-25).
4. **Given** sink lỗi (thư mục không ghi được), **When** log, **Then** `log_sink_errors_total` tăng, readiness = Degraded (ACC-17).
5. **Given** queue đầy, **When** log dồn dập, **Then** log bị bỏ có đếm, request không bị chặn (LOG-06).

### User Story 2 - Trace và metrics (Priority: P1)

Một command tạo một trace nối HTTP → command → giao dịch store; `requestId`/`playerId` là attribute của span, không là
label metric. Metrics OBS-01 có giá trị và xuất qua OTLP khi cấu hình endpoint.

**Acceptance Scenarios**:

1. **Given** exporter in-memory, **When** chạy một command, **Then** span `command <op>` là con của span HTTP cùng trace, có `gs.request_id` (ACC-41).
2. **Given** metrics in-memory, **When** chạy command, **Then** có `txn_attempts_total`, `command_results_total{operation, code}`,
   `http.server.request.duration`; không metric nào có tag `playerId`/`requestId` (OBS-02).

### User Story 3 - Health và shutdown (Priority: P1)

`/health/live` luôn 200 khi process sống; `/health/ready` 200/503 không kèm chi tiết; chi tiết từng check chỉ ở endpoint GM.
Khi dừng, readiness chuyển 503 ngay, request đang chạy hoàn tất trong ≤ 25 s, command đang commit được hoàn tất, log được flush.

**Acceptance Scenarios**:

1. **Given** store không truy cập được, **When** gọi ready từ ngoài, **Then** 503 không chi tiết; live 200; GM thấy check `store` Unhealthy (ACC-51).
2. **Given** command đang ở bước commit, **When** host stop, **Then** command hoàn tất và có receipt (ACC-28).
3. **Given** host đang dừng, **When** gọi ready, **Then** 503.

### User Story 4 - Chạy sau reverse proxy (Priority: P2)

Khi bật ForwardedHeaders với danh sách proxy tin cậy, IP client lấy từ `X-Forwarded-For` chỉ khi request đến từ proxy đó.

**Acceptance Scenarios**:

1. **Given** proxy tin cậy 10.0.0.1, **When** request từ 10.0.0.1 có `X-Forwarded-For: 1.2.3.4`, **Then** RemoteIp = 1.2.3.4; từ IP khác → giữ nguyên.
2. **Given** bật ForwardedHeaders không khai proxy/network, **When** khởi động, **Then** startup fail.

## Requirements *(mandatory)*

- **FR-001**: Log file per instance `{dir}/{gameId}-{instanceId}-{date}.log`, compact JSON, rolling Day + size (mặc định
  100 MiB, 30 file, `retainedFileTimeLimit` tuỳ chọn), shared=false (LOG-01..03).
- **FR-002**: Async sink queue 10 000, không chặn khi đầy, đếm log bị bỏ (`log_dropped_total`); `SelfLog` → `log_sink_errors_total`;
  gauge `log_dir_free_bytes`; health `log` Degraded khi có lỗi sink trong 5 phút gần nhất hoặc đĩa < ngưỡng (LOG-06).
- **FR-003**: Enrich: instance, gameId, environment, RequestId (scope), TraceId/SpanId; che giá trị thuộc tính có tên nhạy cảm
  (token, password, secret, pepper, authorization, cookie, receipt payload) (LOG-04, LOG-05).
- **FR-004**: OpenTelemetry tracing (ASP.NET Core, HttpClient, `GameServer.*` sources) + metrics (`GameServer.*` meters,
  ASP.NET Core hosting/Kestrel/rate limiting, runtime); OTLP khi có `Observability:Otlp:Endpoint` (OBS-01, OBS-04).
- **FR-005**: Metric bổ sung: `command_results_total{operation, code}`, `http_errors_total{code}` (OBS-01).
- **FR-006**: `/health/live` (không check dependency), `/health/ready` (store, config, log, shutdown) — Public, chỉ trả trạng thái;
  `GET /gm/v1/health` (permission `ops.read`) trả chi tiết (OBS-03).
- **FR-007**: Graceful shutdown: `HostOptions.ShutdownTimeout` = 25 s (cấu hình), readiness 503 khi stopping, flush log (OPS-03).
- **FR-008**: `GameServer:Hosting:ForwardedHeaders {Enabled, KnownProxies[], KnownNetworks[], ForwardLimit}`; bật mà rỗng → startup fail (OPS-02).
- **FR-009**: Secret không nằm trong appsettings commit (OPS-08): startup cảnh báo/fail khi `Identity:GuestPepper`/`Couchbase:Password`
  đến từ file appsettings ở Production — chỉ chấp nhận env var/secret provider.

## Success Criteria *(mandatory)*

- **SC-001**: Bộ test quét log không tìm thấy secret nào.
- **SC-002**: Ready trả 503 không chi tiết khi store hỏng; live vẫn 200.
- **SC-003**: Một command có đúng một trace nối HTTP và command.

## Assumptions

- Instance id = `GameServer:InstanceId` hoặc biến `HOSTNAME`/tên máy + pid.
- Log retention theo TBD-08 do ops cấu hình; mặc định như LOG-02.
