# Research: Platform Core (001)

Ngày: 2026-10-26. Fact về Couchbase transactions lấy từ
`AgentMemories/features/req-review/06_verify_couchbase_txn.md` (đã xác minh 22/10/2026); version
package tra trực tiếp NuGet ngày 26/10/2026.

## R1. Runtime và SDK

- **Decision**: target `net10.0`; build bằng .NET SDK **10.0.403** (runtime 10.0.14), pin trong
  `global.json` (`rollForward: latestPatch`).
- **Rationale**: tài liệu yêu cầu 2.2 chốt .NET 10 LTS. Máy dev chỉ có SDK `10.0.100-rc.1` và không có
  ASP.NET Core 10 runtime → đã cài SDK GA vào `%USERPROFILE%\.dotnet10` (dotnet-install, không cần
  admin). Không bật Native AOT/trimming (constitution).
- **Alternatives**: net8.0 (trái yêu cầu); dùng RC1 (thiếu ASP.NET Core runtime, không có bản vá).

## R2. Package (pin trong `Directory.Packages.props`)

| Package | Version | Dùng cho |
| --- | --- | --- |
| CouchbaseNetClient | 3.10.0 | KV, transactions tích hợp (`Couchbase.Client.Transactions`), query |
| Google.Protobuf | 3.36.2 | runtime Protobuf (pin, API-09) |
| Grpc.Tools | 2.84.0 | chỉ `protoc` codegen (`GrpcServices="None"`), `PrivateAssets=all` |
| xunit.v3 | 4.0.2 | test framework; `Assert.Skip` cho integration test thiếu kết nối |
| xunit.runner.visualstudio | 4.0.1 | runner |
| Microsoft.NET.Test.Sdk | 18.11.0 | test host |
| Shouldly | 4.3.0 | assertion |
| NetArchTest.Rules | 1.3.2 | architecture test |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | `WebApplicationFactory` |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | (latest stable lúc implement) | cấm `.Result`, `.Wait()`, `DateTime.UtcNow` |

- **Rationale**: SDK Couchbase ≥ 3.9.1 bắt buộc (V23/V17); 3.10.0 là bản mới nhất và sửa lỗi flags
  của binary doc trong transaction. Không dùng package `Couchbase.Transactions` (deprecated).

## R3. Ranh giới project

- **Decision**: tách `GameServer.Platform` (runtime nghiệp vụ của framework: registry, validator,
  command runner, outbox, config) khỏi `GameServer.Platform.Couchbase` và `GameServer.Platform.Web`.
  Runner viết trên abstraction `ITransactionalDocumentStore` nhỏ; Couchbase và in-memory là hai
  implementation.
- **Rationale**: (1) cho phép unit test toàn bộ pipeline idempotency/transaction bằng store in-memory
  (owner chọn fake khi chưa có Couchbase); (2) một bộ **store conformance test** chạy trên cả hai
  implementation để giữ hành vi giống nhau; (3) vẫn đúng yêu cầu "mọi ghi `pa_*`/`rcpt::*` chỉ qua
  transaction runner" vì store chỉ được gọi bởi runner/writer (arch test).
- **Alternatives**: runner nằm thẳng trong Platform.Couchbase (đúng chữ tài liệu 4.1 nhưng không test
  được không có DB); repository per aggregate (nhiều code, dễ ghi ngoài transaction).
- **Ghi chú tài liệu**: tài liệu yêu cầu 4.1 đặt `ICommandRunner` trong Platform.Couchbase. Plan giữ
  interface và trách nhiệm, chỉ tách logic DB-agnostic ra `GameServer.Platform`; Platform.Couchbase vẫn
  là nơi duy nhất chạm SDK. Ghi ADR-0001.

## R4. Đọc aggregate trong transaction

- **Decision**: `ITransactionContext.GetManyAsync(keys)` → Couchbase: `ctx.GetMulti(...)` (≥ 3.8.0, API
  volatile) bọc trong `CouchbaseGetMultiAdapter`, mode ưu tiên latency; fallback tuần tự `GetOptionalAsync`
  khi API không dùng được. Receipt key nằm trong cùng lần `GetMulti` với aggregate (1 RTT cho cả tra
  replay). Không `Task.WhenAll` trên `ctx.GetAsync`.
- **Rationale**: DAT-11, V23. Chữ ký chính xác của `GetMulti` xác nhận lúc implement theo API ref 3.10;
  test V23-T1 trong TST-04 đo RTT.

## R5. Business rejection có tạo receipt không

- **Decision**: **không**. Handler trả lỗi nghiệp vụ (`INSUFFICIENT_FUNDS`, `AGGREGATE_TOO_LARGE`,
  mã của module) → lambda rollback, không ghi gì, response 200 với `code`. Chỉ command thành công mới
  có receipt.
- **Rationale**: tài liệu yêu cầu chỉ bắt buộc receipt cho thay đổi trạng thái (CMD-03); retry cùng
  `requestId` sau khi điều kiện thay đổi (ví dụ đã nạp thêm tiền) là cùng ý định của người chơi; giữ
  receipt tối thiểu. Invariant "một lần" do claim marker/state bảo đảm (CMD-07).
- **Alternatives**: lưu receipt thất bại (tốn ghi, khoá requestId vĩnh viễn 24 h).
- **Hệ quả**: ghi trong PCR `platform.md`.

## R6. Claim marker trùng

- **Decision**: trong lambda đọc marker bằng `GetOptional` (gộp vào `GetMulti` khi key biết trước) →
  có thì trả kết quả "đã tồn tại" cho handler; không có thì `Insert`. Không dựa vào việc bắt
  `DocumentExistsException` trong lambda.
- **Rationale**: hành vi bắt lỗi Insert trong lambda .NET không được tài liệu hoá rõ; insert đồng thời
  bị write-write conflict của transaction xử lý (retry, lần sau thấy marker).

## R7. `requestId` và `bodyHash`

- **Decision**: `requestId` (UUID) ở header `X-Request-Id`; `bodyHash` = SHA-256 trên raw body bytes.
  Cùng header dùng làm correlation id (OBS-04: "gắn `requestId` từ header"). Request không có header
  hợp lệ: command → 400 `BAD_REQUEST`; query → server tự sinh correlation id.
- **Rationale**: pipeline idempotency không phải decode body trước khi tra receipt; SDK gửi lại đúng
  bytes (SDK-02) nên hash ổn định.

## R8. Error code

- **Decision**: `ErrorCode { int Number; string Name; bool Retryable; string SafeMessage }`; core dải
  1–999; mỗi module khai `ErrorCodeRange` trong descriptor (ví dụ counter 10000–10099); registry phát
  hiện trùng tên, trùng số, chồng dải ở startup. Wire: `ErrorEnvelope.code` (string name) +
  `code_number` (int32). Bảng sinh ra trong PCR.
- **Rationale**: API-11 nói "Protobuf enum + bảng PCR"; một enum Protobuf trung tâm buộc sửa Core khi
  thêm module (trái EXT/ARC-03). String name + số theo dải cho SDK map được mà không đổi proto core.
- **Alternatives**: enum per module trong proto (khó gộp vào một field của envelope).

## R9. Wire model Player

- **Decision**: Command `POST /api/v1/{module}/{operation}` body = message request của module;
  response `CommandResponse { code, code_number, receipt_id, repeated SectionUpdate updates, bytes
  result, config_version, server_time_utc }` — `result` là message kết quả của module đã serialize, cũng
  là `resultCompact` lưu trong receipt (≤ 512 B) để replay trả byte-identical. Snapshot
  `POST /api/v1/player/snapshot`; receipt `GET /api/v1/player/receipts/{receiptId}`; time
  `GET /v1/time` (Public). Aggregate state lưu JSON trong DB, wire là Protobuf qua mapper per aggregate.
- **Rationale**: API-08, API-10, CMD-04 (replay trả cùng kết quả), TIME-01.

## R10. Lưu aggregate

- **Decision**: document JSON (System.Text.Json): `{ gameId, environmentId, playerId, aggregateId,
  schemaVersion, rev, createdAtUtc, updatedAtUtc, state }`; key = `playerId`; collection
  `g_<gameId>.pa_<aggregateId>`. Upgrader chain thao tác trên `JsonNode` của `state`.
- **Rationale**: DAT-02, DAT-10; JSON dễ đọc cho GM/ops; Protobuf chỉ trên wire.

## R11. Pipeline HTTP

- **Decision**: thứ tự middleware: exception → correlation → gate slot → authentication →
  deny-by-default profile check → authorization → request timeout → rate limiter (nhóm) → endpoint
  (decode/validate → per-player limiter → runner). Body limit per-endpoint qua
  `IHttpMaxRequestBodySizeFeature` (Player 64 KiB, GM 1 MiB; Kestrel global = 1 MiB). Lỗi từ
  middleware ghi bằng `IErrorResponseWriter` chọn Protobuf hay Problem Details theo profile của
  endpoint (hoặc prefix `/api/` khi không match endpoint).
- **Rationale**: API-04 (lỗi middleware cũng là ErrorEnvelope), GATE-01/02 (trước auth), PERF-04.

## R12. Startup validation

- **Decision**: `GameServerStartupValidator` chạy sau khi map endpoint, trước `app.RunAsync`: kiểm
  `EndpointDataSource` (profile đúng một, route có `/v{n}/`, trùng route+method, Public trong
  allowlist, `Dev` ở Production), registry (command/error code/aggregate), descriptor (dependency,
  config bắt buộc, gameId/env). Lỗi → `GameServerStartupException` liệt kê mọi vi phạm; host exit ≠ 0.
  Thêm middleware deny-by-default phòng validator bị tắt.
- **Rationale**: SEC-08, ARC-06, ARC-07, ACC-12, ACC-26.

## R13. Xác thực thử nghiệm

- **Decision**: scheme `GameServerTest` đọc header `X-Test-Player: <playerId>` hoặc `X-Test-Gm:
  <staffId>` + `X-Test-Gm-Permissions`; chỉ đăng ký khi environment ≠ Production (validator kiểm).
  Bị thay bởi session thật ở slice 002 và GM auth ở 004.

## R14. Outbox relay/sweeper

- **Decision**: sau commit, runner gọi `IOutboxRelay.RelayAsync(receipt)` (best-effort, lỗi chỉ log +
  metric); consumer theo `kind` ghi đích bằng insert-if-absent theo `entryId`; sau khi mọi entry xong,
  store đặt `outboxPending=false` (Couchbase: `MutateIn` ngoài transaction trên receipt đã commit — tài
  liệu yêu cầu DAT-09 tính sẵn "1 MutateIn receipt" trong chi phí chuẩn). Sweeper `BackgroundService`
  mỗi 30 s quét receipt pending cũ hơn 10 s (N1QL qua partial index `receipts(outboxPending)` với
  `request_plus`; in-memory: scan), relay giống hệt.
- **Rationale**: JOB-02, OPS-05, PERF-01. Cập nhật cờ ngoài transaction an toàn vì receipt đã commit
  không còn bị transaction nào ghi (chỉ đọc khi replay). Purge (slice sau) sẽ dùng writer chuyên dụng.

## R15. Store in-memory

- **Decision**: optimistic concurrency: mỗi document có version; transaction ghi buffer, đọc ghi lại
  version; commit dưới một lock: kiểm version của mọi document đã đọc/ghi → conflict thì retry lambda
  (tới hết `ExpirationTime`) → `TransactionExpired`. Fault injection: `FailBeforeCommit`,
  `AmbiguousAfterCommit` (commit rồi ném ambiguous), `AmbiguousBeforeCommit`, `DelayCommit`. Đọc
  ngoài transaction chỉ thấy dữ liệu đã commit.
- **Rationale**: tái hiện các ngữ nghĩa CMD-03/04/05 để unit test ACC-04/05/07/44.

## R16. Kiểm tra tĩnh

- **Decision**: BannedApiAnalyzers cấm `Task.Wait`, `Task<T>.Result`, `GetAwaiter().GetResult()`,
  `DateTime.UtcNow/Now`, `DateTimeOffset.UtcNow/Now` trong mọi project framework/module (PERF-01,
  TIME-01); NetArchTest cho dependency (ARC-01, CMD-05: chỉ Platform.Couchbase phụ thuộc
  `Couchbase.*`; không type kế thừa `ControllerBase`).
- **Rationale**: arch test của tài liệu yêu cầu; analyzer bắt được lời gọi mà NetArchTest không thấy.

## R17. Integration test không có Couchbase

- **Decision**: biến môi trường `COUCHBASE_CONNSTR`, `COUCHBASE_USERNAME`, `COUCHBASE_PASSWORD`,
  `COUCHBASE_BUCKET`; thiếu → `Assert.Skip("COUCHBASE_CONNSTR not set …")`. Store conformance test và 8
  test TST-04 nằm trong `tests/GameServer.Platform.Couchbase.IntegrationTests`.
- **Rationale**: owner chọn 26/10/2026; constitution VI (skip có lý do, không giả pass).
