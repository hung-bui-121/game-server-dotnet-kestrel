# HTTP routes — slice 001

Player: `application/x-protobuf`, lỗi `ErrorEnvelope`. GM: `application/json`, lỗi Problem Details
(`type`, `title`, `status`, `detail`, extension `code`, `codeNumber`, `requestId`, `retryable`,
`retryAfterMs`). Mọi Player response có `server_time_utc_ms` (TIME-01).

| Method | Route | Profile | Body → Response | Ghi chú |
| --- | --- | --- | --- | --- |
| GET | `/v1/time` | Public | — → `TimeResponse` | allowlist Public (SEC-09) |
| POST | `/api/v1/player/snapshot` | Player | `SnapshotRequest` → `SnapshotResponse` | PLY-02, API-10 |
| GET | `/api/v1/player/receipts/{receiptId}` | Player | — → `ReceiptView` | CMD-09; chỉ actor sở hữu; khác actor → 404 |
| POST | `/api/v1/counter/increment` | Player | `IncrementRequest` → `CommandResponse` | header `X-Request-Id` (UUID) bắt buộc |
| POST | `/api/v1/counter/get` | Player | `GetCounterRequest` → `GetCounterResponse` | query |
| GET | `/gm/v1/counter/{playerId}` | GM (`counter.read`) | — → JSON `{playerId, value, rev}` | ví dụ GM JSON + permission |

## Header

| Header | Hướng | Ý nghĩa |
| --- | --- | --- |
| `X-Request-Id` | req | UUID; bắt buộc cho command; correlation cho mọi request |
| `X-Client-Version`, `X-Platform`, `X-Build-Id` | req | GATE-02 slot (chưa enforce ở 001) |
| `X-Request-Id` | resp | echo correlation id |
| `Retry-After` | resp | giây, khi 429/503 |
| `X-Test-Player`, `X-Test-Gm`, `X-Test-Gm-Permissions` | req | chỉ scheme thử nghiệm (Development/test) |

## Status mapping (API-05)

| Tình huống | Status | `code` |
| --- | --- | --- |
| Thành công / lỗi nghiệp vụ | 200 | `OK` / mã module, `AGGREGATE_TOO_LARGE` |
| Request sai, thiếu `X-Request-Id` | 400 | `BAD_REQUEST` |
| Chưa xác thực | 401 | `UNAUTHENTICATED` |
| Thiếu quyền / endpoint không có profile | 403 | `FORBIDDEN` |
| Không tìm thấy | 404 | `NOT_FOUND` |
| Payload khác cùng requestId | 409 | `IDEMPOTENCY_PAYLOAD_MISMATCH` |
| Conflict hết hạn | 409 | `CONCURRENT_UPDATE` (retryable) |
| Body quá lớn | 413 | `PAYLOAD_TOO_LARGE` |
| Sai content type | 415 | `UNSUPPORTED_MEDIA_TYPE` |
| Quá tải limiter | 429 | `RATE_LIMITED` (retryable, `Retry-After`) |
| Commit không rõ | 503 | `COMMIT_UNKNOWN_RETRY` (retryable) |
| Timeout request | 503 | `REQUEST_TIMEOUT` (retryable) |
| Lỗi khác | 500 | `INTERNAL` |
