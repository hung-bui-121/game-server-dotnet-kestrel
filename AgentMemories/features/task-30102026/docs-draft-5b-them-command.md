# Bản nháp nội dung 5b — Thêm, bỏ, thay command (API) và đường đi của một request

> **Đúng với commit 175df65** (nhánh `012-analytics-audit`, tài liệu làm trên `docs-html-guides`).
> Trang HTML đích: `docs/html/02-them-command.html`. Footer của trang phải ghi "đúng với commit 175df65".

## Ghi chú cho người dựng HTML (frontend-web-dev)

- Văn xuôi tiếng Việt; identifier giữ nguyên trong `<code>`. Đường dẫn file tương đối từ gốc repo.
- Số dòng ghi dạng "≈ dòng N" chỉ để tham khảo (sẽ lệch); tham chiếu chính là tên type/method.
- Cần vẽ **Sơ đồ C** (sequence một command, mục 2) bằng SVG inline. Mục 1 có một **bảng đối chiếu** là nội dung quan trọng nhất
  trang: nên làm nổi bật (khung màu, đặt ngay đầu trang).
- Code snippet ở mục 4 được **trích nguyên văn** từ repo (trừ chỗ ghi rõ "code mới cần viết" ở mục 5).
  Dùng `<pre><code>` có highlight tối thiểu, không cần thư viện ngoài.

---

## 1. Điều quan trọng nhất: handler **không** đọc hay ghi database

Mô hình quen thuộc khi viết API: *nhận message → phân loại → xử lý logic → đọc param → **get DB** → update → **save DB** → trả
response*. Trong framework này, các bước đó vẫn có đủ, nhưng **ba bước liên quan tới database do framework làm**, không phải
code của command:

- **Trước** khi gọi handler, `CommandRunner` mở một transaction và **đọc sẵn** mọi aggregate khai trong `Touches` và `Reads` của
  command (cộng receipt để chống chạy trùng).
- Handler chỉ làm việc với `ICommandContext`: `ctx.Get<T>(id)` lấy bản sao đã đọc, `ctx.Set(id, newState)` ghi vào **bộ đệm**.
- **Sau** khi handler trả `Ok`, `CommandRunner` ghi các aggregate đã đổi với `rev + 1`, ghi **receipt**, và **commit** tất cả trong
  cùng một transaction. Nếu handler trả `Reject`, transaction rollback — không có gì được ghi.
- Client gửi lại cùng `X-Request-Id` → framework trả lại kết quả cũ từ receipt (replay), handler không chạy lần hai.

Vì vậy handler phải "thuần": không gọi HTTP, không gọi DB, không đọc giờ hệ thống; transaction có thể chạy lại handler nhiều lần
khi có xung đột (comment của `ICommandHandler` trong `src/GameServer.Core/Commands/ICommandHandler.cs`).

### Bảng đối chiếu: mỗi bước trong mô hình của bạn nằm ở đâu trong code

| Bước (mô hình quen thuộc) | Ai làm | Ở đâu trong code | Ví dụ với `counter.increment` |
| --- | --- | --- | --- |
| Nhận message | Framework | Kestrel → middleware pipeline, `GameServerApplicationExtensions.UseGameServer` (`src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`) | `POST /api/v1/counter/increment` |
| **Phân loại** (message nào, xử lý bởi ai) | Framework, theo khai báo của module | (1) Routing chọn endpoint theo route — endpoint được tạo bởi `MapPlayerCommand` với tên operation; (2) `CommandRunner.ExecuteCoreAsync` gọi `ModuleRegistry.GetCommand(operation)` lấy `CommandRegistration` (descriptor + kiểu handler) | route → operation `"counter.increment"` → `IncrementHandler` |
| **Đọc param** | Module (adapter) viết hàm `ToDomain`; framework gọi | `CommandEndpointFactory.MapCommand`: `ProtobufBinding.ReadBodyAsync` → `ProtobufBinding.Parse<TWireRequest>` → `toDomain(wire)` (`src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`) | `CounterModule.ToDomain`: kiểm `note` ≤ 64 ký tự, tạo `IncrementCommand(By, Note)` |
| **Get DB** | **Framework** | `CommandRunner.ExecuteTransactionAsync` dựng danh sách key từ `Touches ∪ Reads` + receipt; `RunAttemptAsync` gọi `tx.GetManyAsync(keys)` rồi `AggregateDocumentSerializer.Load` (state mặc định nếu chưa có, nâng schema lười) | đọc `pa_counter/{playerId}` + `receipts/rcpt::{receiptId}` |
| **Xử lý logic cụ thể** + **update** | **Module** (handler) | `IncrementHandler.HandleAsync`: kiểm rule, `context.Get<CounterState>(...)`, `context.Set(...)`, `context.AddOutbox(...)` | `By <= 0` → `Reject(InvalidAmount)`; `By > 10` → `Reject(LimitExceeded)`; còn lại `Value += By` |
| **Save DB** | **Framework** | `CommandRunner.RunAttemptAsync` sau handler: so state mới với cũ, `rev + 1`, `tx.InsertAsync`/`tx.ReplaceAsync` aggregate, insert claim marker, dựng và insert receipt; commit khi `store.RunTransactionAsync` kết thúc | ghi `pa_counter` rev N+1 + receipt (có outbox `counter_log`) |
| Hiệu ứng phụ sau commit | Framework | `OutboxRelay.TryRelayAsync` (inline) / `OutboxSweeper` (nền) | entry `counter_log` được chiếu vào collection `counter_log` |
| **Response cho client** | Framework | `CommandEndpointFactory.BuildResponse` → `ProtobufBinding.WriteAsync`; lỗi → `ExceptionMiddleware` → `ErrorResponseWriter` (`ErrorEnvelope`) | `CommandResponse{code="OK", result=IncrementResult, updates=[counter]}` |

Tóm lại, để thêm một command, bạn viết: **khai báo** (`CommandDescriptor` với `Touches`/`Reads`), **handler** (logic), **hàm
`ToDomain`** (đọc param), **hàm map kết quả sang wire**, và **một dòng map route**. Bạn không viết code đọc/ghi DB, không viết
transaction, không viết idempotency.

## 2. Sơ đồ sequence một command (Sơ đồ C — cần vẽ SVG)

**Lifeline** (trái → phải):
1. `Unity: GameServerClient`
2. `ASP.NET pipeline` (gom các middleware)
3. `Endpoint (CommandEndpointFactory)`
4. `CommandRunner`
5. `Handler (IncrementHandler)`
6. `Couchbase (transaction)`
7. `OutboxRelay`

**Thông điệp** (đánh số đúng như mục 3):

| # | Từ → Tới | Nhãn |
| --- | --- | --- |
| 1 | Unity → pipeline | `POST /api/v1/counter/increment` (Protobuf; `X-Request-Id`, `Authorization: Bearer`, `X-Client-Version`, `X-Platform`, `X-Build-Id`) |
| 2 | pipeline (tự gọi) | `ExceptionMiddleware` → `CorrelationMiddleware` → routing → `GateMiddleware` → `ProfileEnforcementMiddleware` (xác thực Bearer) → timeout → rate limiter |
| 3 | pipeline → Endpoint | gọi delegate của route |
| 4 | Endpoint (tự gọi) | đọc body ≤ 64 KiB, kiểm requestId, parse Protobuf, `ToDomain` (đọc + validate param) |
| 5 | Endpoint → CommandRunner | `ExecuteAsync(CommandInvocation)` |
| 6 | CommandRunner (tự gọi) | tra command, kiểm actor, khoá theo player, tính `receiptId` |
| 7 | CommandRunner → Couchbase | `GetAsync(receipt)` (ngoài transaction) |
| 7a | Couchbase → CommandRunner (nhánh `alt`: đã có receipt) | receipt → **replay**, nhảy tới #13 |
| 8 | CommandRunner (tự gọi) | `ICommandGuard` (ban theo nhóm command) |
| 9 | CommandRunner → Couchbase | `RunTransactionAsync` bắt đầu; `GetManyAsync(Touches ∪ Reads + receipt)` |
| 10 | CommandRunner → Handler | `HandleAsync(request, ctx)` |
| 10a | Handler → CommandRunner | `ctx.Get` / `ctx.Set` / `ctx.AddOutbox` (chỉ chạm bộ đệm trong RAM) |
| 10b | Handler → CommandRunner | trả `CommandOutcome.Ok(result)` hoặc `Reject(code)` (nhánh `alt` Reject: rollback, nhảy tới #13 với mã nghiệp vụ) |
| 11 | CommandRunner → Couchbase | encode result; Insert/Replace aggregate (`rev+1`), Insert claim, Insert receipt; **commit** |
| 12 | CommandRunner → OutboxRelay | `TryRelayAsync` (best effort; lỗi thì sweeper làm sau) |
| 13 | CommandRunner → Endpoint | `CommandExecution` (code, receiptId, result bytes, updates) |
| 14 | Endpoint → Unity | `CommandResponse` (Protobuf, HTTP 200) |
| 14' | pipeline → Unity (nhánh lỗi) | `ErrorEnvelope` (HTTP 4xx/5xx) từ `ExceptionMiddleware` |

Vẽ một khung `loop "tối đa 3 vòng"` bao #7–#11 (runner chạy lại khi đua insert receipt/claim hoặc stale read), và một khung
`transaction (có thể chạy lại handler)` bao #9–#11.

## 3. Đường đi chi tiết khi nhận một command

Tham chiếu: `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` (thứ tự middleware ≈ dòng 64–80),
`src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`, `src/GameServer.Platform/Commands/CommandRunner.cs`.

### Bước 1 — Unity gửi request

`GameServerClient.SendCommandAsync(route, request, resultParser)` (`sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs`):
serialize body **một lần**, gửi `POST` Protobuf với header `X-Request-Id` (UUID mới), `X-Client-Version`, `X-Platform`,
`X-Build-Id`, `Authorization: Bearer <token>`. Khi retry (lỗi mạng, `COMMIT_UNKNOWN_RETRY`, `CONCURRENT_UPDATE`, `RATE_LIMITED`…)
gửi lại **đúng bytes và đúng `X-Request-Id`**.

### Bước 2 — Kestrel và `ForwardedHeaders`

Kestrel nhận request. Nếu cấu hình reverse proxy (`GameServer:Hosting:ForwardedHeaders`), `UseForwardedHeaders` chạy đầu tiên để IP
client lấy từ proxy tin cậy.

### Bước 3 — `ExceptionMiddleware` (ngoài cùng)

`src/GameServer.Platform.Web/Middleware/ExceptionMiddleware.cs`. Bọc toàn bộ phần sau. Đổi exception thành response lỗi:
`GameServerException` → mã trong catalog; body Protobuf hỏng → 400 `BAD_REQUEST`; body quá lớn → `PAYLOAD_TOO_LARGE`; exception
lạ → 500 `INTERNAL` (không lộ chi tiết). Route không tồn tại → 404 `NOT_FOUND`. Ghi bằng `ErrorResponseWriter` (Protobuf
`ErrorEnvelope` cho Player API, JSON cho GM).

### Bước 4 — `CorrelationMiddleware`

`src/GameServer.Platform.Web/Middleware/CorrelationMiddleware.cs`. Đọc `X-Request-Id`: UUID hợp lệ → dùng làm correlation id **và**
requestId của command; không hợp lệ → server tự sinh id chỉ để log (command sẽ bị từ chối ở bước 8). Trả lại header
`X-Request-Id`, gắn vào log scope và trace.

### Bước 5 — Routing (**phân loại lần 1**)

`app.UseRouting()` chọn endpoint theo method + route. Endpoint của command được tạo lúc khởi động bởi `MapPlayerCommand`
(bước 8), mang theo tên operation (vd. `"counter.increment"`) và metadata profile `Player`, gate, timeout, rate limiter.

### Bước 6 — `GateMiddleware` (trước xác thực)

`src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`. Chỉ áp cho endpoint có `GateMetadata` (Player API và login).
`StoreGateEvaluator` đọc document `gate` (cache ≤ `GameServer:Gate:CacheSeconds`, mặc định 10 s): đang bảo trì → 503
`MAINTENANCE`; `X-Client-Version` thấp hơn tối thiểu của `X-Platform` → 426 `UPDATE_REQUIRED`.

### Bước 7 — `ProfileEnforcementMiddleware` (xác thực + phân quyền)

`src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs`. Pipeline **không** dùng `UseAuthentication`; middleware này
xác thực theo profile của endpoint:
- Endpoint không có profile → 403 (deny-by-default).
- Đặt giới hạn body theo profile (`GameServer:Limits:PlayerBodyBytes` = 64 KiB).
- Profile `Player` → gọi scheme `PlayerBearer` = `PlayerBearerAuthenticationHandler`
  (`src/GameServer.Modules.Identity.Adapters/PlayerBearerAuthenticationHandler.cs`): token → session → profile; kiểm theo thứ tự
  tài khoản đã xoá (`ACCOUNT_DELETED`) → bị khoá login (`ACCOUNT_SUSPENDED`) → phiên cũ (`session.Epoch < profile.SessionEpoch` →
  `SESSION_REPLACED`). Thất bại → `UNAUTHENTICATED` hoặc mã tương ứng.
- Profile `Gm` → cookie GM, CSRF cho thao tác ghi, permission của endpoint.

Sau đó `UseRequestTimeouts` (Player 10 s) và `UseRateLimiter` (concurrency command toàn instance, mặc định 200 + hàng đợi; quá thì
`RATE_LIMITED`).

### Bước 8 — Endpoint của `MapPlayerCommand` (**đọc param**)

`CommandEndpointFactory.MapCommand` (≈ dòng 88–130):
1. `ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes)` — đọc raw body.
2. `CorrelationMiddleware.GetClientRequestId(http)` — thiếu hoặc không phải UUID → 400 `BAD_REQUEST`.
3. `ProtobufBinding.Parse<TWireRequest>(body)` — message wire (vd. `Sample.Counter.Protocol.V1.IncrementRequest`).
4. `ActorContextAccessor.GetActor(http)` — actor (playerId) từ bước 7.
5. `ReceiptIds.BodyHash(body)` — SHA-256 của raw body (để phát hiện cùng requestId mà khác body).
6. **`toDomain(wire)`** — hàm của module: validate và đổi wire → record domain (vd. `IncrementCommand`). Lỗi → ném
   `GameServerException(CoreErrorCodes.BadRequest, ...)` → 400.
7. Tạo `CommandInvocation(operation, actor, requestId, bodyHash, request, encodeResult)` rồi gọi `ICommandRunner.ExecuteAsync`.

Biến thể `toDomainAsync` (IAP, `identity.link`): gọi `runner.TryReplayAsync` **trước** để retry không gọi lại store/provider.

### Bước 9 — `CommandRunner` (**phân loại lần 2, get DB, logic, save DB**)

`src/GameServer.Platform/Commands/CommandRunner.cs`:

**9.1 `ExecuteCoreAsync`** (≈ dòng 55–113)
- `registry.GetCommand(invocation.Operation)` → `CommandRegistration` (descriptor + kiểu handler). Operation không đăng ký → 404
  `NOT_FOUND`.
- `EnsureActorAllowed`: command Player chỉ cho actor Player, command GM cần permission.
- `limiter.AcquireAsync(playerId)` (`PerPlayerCommandLimiter`): mỗi player chạy **một** command một lúc trên instance, hàng đợi
  `GameServer:Commands:PerPlayerQueueLimit` (2); đầy → 429 `RATE_LIMITED`.
- `ReceiptIds.Compute(actorId, playerId, operation, requestId)` → `receiptId`, key `receipts/rcpt::{receiptId}`.
- **Đường replay nhanh** (ngoài transaction): `store.GetAsync(receiptKey)`; có receipt trong replay window (24 h,
  `GameServer:Commands:ReplayWindowHours`) → `ReplayAsync`: body hash khác → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`; giống → trả
  `code`, `receiptId`, `result` y hệt lần đầu, `replayed = true`, kèm state **hiện hành** của các aggregate client-visible.
- Các `ICommandGuard` (vd. `BanCommandGuard`, `src/GameServer.Modules.Identity.Adapters/BanCommandGuard.cs`) — chỉ chặn lần chạy mới;
  bị chặn → `ACCOUNT_SUSPENDED`.
- Gọi `ExecuteTransactionAsync`; đua insert receipt/claim (`DocumentExistsException`) hoặc `StaleReadException` → chạy lại tối đa
  3 vòng (vòng sau thường gặp receipt và replay); hết → 409 `CONCURRENT_UPDATE`.

**9.2 `ExecuteTransactionAsync`** (≈ dòng 161–239)
- Dựng `keys` = aggregate trong `Touches ∪ Reads` (key `pa_{aggregateId}/{playerId}`) + receipt key.
- `store.RunTransactionAsync(tx => RunAttemptAsync(...), ExpirationSeconds = 10)`. Từ đây **không** dùng token huỷ của request:
  client ngắt kết nối không làm hỏng commit.
- Lỗi transaction: commit không rõ → 503 `COMMIT_UNKNOWN_RETRY`; hết hạn → 409 `CONCURRENT_UPDATE`; thất bại → 500 `INTERNAL`.

**9.3 `RunAttemptAsync`** (≈ dòng 241–347) — một lần thử trong transaction:
1. **Get DB:** `tx.GetManyAsync(keys)`. Nếu receipt đã có (instance khác vừa commit) → replay.
2. `serializer.Load(descriptor, playerId, document)` cho từng aggregate: không có document → state mặc định (`createDefault`),
   `rev = 0`; `schemaVersion` cũ → chạy upgrader; mới hơn code → 500.
3. Tạo `CommandContext` (actor, `now` = `IClock.UtcNow` cố định cho lần thử, config, aggregate đã đọc).
4. `ActivatorUtilities.CreateInstance(services, registration.HandlerType)` rồi `registration.InvokeAsync(handler, request, ctx)`
   → **handler chạy logic**.
5. Outcome không OK → ném nội bộ → **rollback**, không receipt. Mã HTTP 200 (mã nghiệp vụ) → `CommandResponse` với `code`; mã khác
   200 → `ErrorEnvelope`.
6. `invocation.EncodeResult(result)` → bytes kết quả (hàm `toWire` của module chạy **ở đây**, trong transaction); > 512 B
   (`GameServer:Commands:ResultCompactMaxBytes`) → lỗi lập trình (500).
7. **Save DB:** với mỗi aggregate trong `Touches` mà handler đã `Set` **và** state thực sự đổi: `newRev = rev + 1`,
   `serializer.BuildDocument` (> 64 KiB → `AGGREGATE_TOO_LARGE`), rồi `tx.InsertAsync` (document mới) hoặc `tx.ReplaceAsync`.
   Aggregate không đổi giữ nguyên `rev`.
8. Insert claim marker (`ctx.TryClaimAsync`).
9. `EnsureAudit`: command GM phải có đúng 1 audit, command khác 0.
10. `BuildReceipt` (requestId, bodyHash, resultCode, resultCompact, các aggregate đã ghi và rev mới, outbox, commit time) — > 4 KiB →
    500; insert receipt. Kết thúc lambda → store **commit**.

**9.4 Sau commit** (≈ dòng 212–238)
- `relay.TryRelayAsync(...)` nếu receipt có outbox (`GameServer:Outbox:InlineRelayEnabled`); thất bại thì `OutboxSweeper` giao lại.
- Dựng `AggregateChange` cho các aggregate **client-visible** đã ghi → `CommandExecution`.

### Bước 10 — Response

`CommandEndpointFactory.BuildResponse` (≈ dòng 132–157): `CommandResponse` (`proto/gameserver/core/v1/core.proto`) gồm `code`,
`code_number`, `receipt_id` (rỗng khi bị từ chối), `result` (bytes kết quả của module), `config_version`, `server_time_utc_ms`,
`replayed`, `updates` (mỗi aggregate đổi: `id`, `base_rev`, `new_rev`, `payload` qua wire mapper đăng ký bằng
`AddAggregateWireMapper`, `full = true`). Ghi bằng `ProtobufBinding.WriteAsync`, HTTP 200.

Lỗi ở bất cứ bước nào → `ExceptionMiddleware` ghi `ErrorEnvelope` (`code`, `code_number`, `message`, `request_id`, `retryable`,
`retry_after_ms`, `server_time_utc_ms`, `config_version`, `details`).

### Bước 11 — Unity nhận kết quả

`SendCommandAsync` áp `updates` vào `client.Sections` theo `rev`; nếu lệch thì tự gọi snapshot cho section đó; parse `result` khi
`code == "OK"`. Sau khi server đã trả lời, phương thức **không ném lỗi** (báo qua `SectionsStale`, `ResultUnreadable`) để game không
vô tình gửi lại bằng requestId mới.

### Bảng tóm tắt mã trả về

| Tình huống | HTTP | Body | Có receipt? |
| --- | --- | --- | --- |
| Thành công | 200 | `CommandResponse`, `code = "OK"` | có |
| Từ chối nghiệp vụ (`Reject` với mã HTTP 200, vd. `COUNTER_LIMIT_EXCEEDED`) | 200 | `CommandResponse`, `code` = mã, `receipt_id` rỗng, không `updates` | **không** (retry được xử lý như mới) |
| Commit kèm mã (`CommandOutcome.Commit(result, code)`) | 200 | `CommandResponse`, `code` = mã, có `updates` | có |
| Replay cùng body | 200 | như lần đầu, `replayed = true` | (dùng receipt cũ) |
| Cùng requestId, khác body | 409 | `ErrorEnvelope` `IDEMPOTENCY_PAYLOAD_MISMATCH` | — |
| Thiếu/sai `X-Request-Id`, body sai, `ToDomain` từ chối | 400 | `ErrorEnvelope` `BAD_REQUEST` | — |
| Bảo trì / client cũ | 503 / 426 | `MAINTENANCE` / `UPDATE_REQUIRED` | — |
| Commit không rõ | 503 | `COMMIT_UNKNOWN_RETRY` (retryable) | có thể đã commit → retry cùng bytes sẽ replay |

Test minh hoạ: `tests/GameServer.Platform.Web.Tests/CommandPipelineTests.cs`.

## 4. Walkthrough: module mẫu `Sample.Counter` (code thật)

`modules/Sample.Counter` có một command `counter.increment`. Các mảnh dưới đây là **toàn bộ** những gì cần viết cho một command.
Contract: `docs/pcr/sample-counter.md`.

### 4.1 File `.proto` — contract wire

`modules/Sample.Counter/Sample.Counter.Adapters/Protos/sample_counter.proto` (trích):

```proto
syntax = "proto3";

package sample.counter.v1;

option csharp_namespace = "Sample.Counter.Protocol.V1";

// State của aggregate `counter` trên wire (Section.payload).
message CounterState {
  int64 value = 1;
  int32 increments = 2;
}

// POST /api/v1/counter/increment  (header X-Request-Id bắt buộc)
message IncrementRequest {
  int64 by = 1;
  string note = 2;  // tuỳ chọn, ≤ 64 ký tự; đi vào outbox counter_log
}

// CommandResponse.result
message IncrementResult {
  int64 value = 1;
}
```

Biên dịch bằng `Grpc.Tools` trong `Sample.Counter.Adapters.csproj`:

```xml
<Protobuf Include="Protos\sample_counter.proto" GrpcServices="None" />
```

Ghi chú: chỉ cần message request và result của command. Wrapper chung (`CommandResponse`, `SectionUpdate`, `ErrorEnvelope`) nằm ở
`proto/gameserver/core/v1/core.proto`, module không định nghĩa lại.

### 4.2 Aggregate, record command/result, error code — project nghiệp vụ

`modules/Sample.Counter/Sample.Counter/CounterModel.cs` (trích):

```csharp
public sealed record CounterState
{
    public long Value { get; init; }

    public int Increments { get; init; }
}

public sealed record IncrementCommand(long By, string? Note);

public sealed record IncrementResult(long Value);

public static class CounterAggregates
{
    public const string Counter = "counter";

    public static PlayerAggregateDescriptor<CounterState> CounterDescriptor { get; } =
        new(Counter, schemaVersion: 2, clientVisible: true, createDefault: () => new CounterState(), upgraders: [V1ToV2]);
}

public static class CounterErrors
{
    public static readonly ErrorCode LimitExceeded = new(10001, "COUNTER_LIMIT_EXCEEDED", 200, false, "The increment exceeds the allowed amount.");
    public static readonly ErrorCode InvalidAmount = new(10002, "COUNTER_INVALID_AMOUNT", 200, false, "The increment must be positive.");

    public static ErrorCodeSet All { get; } = new(10000, 10099, LimitExceeded, InvalidAmount);
}
```

- Record command/result là **type domain**, không phải message Protobuf (test kiến trúc
  `Command_handlers_receive_domain_types_not_wire_messages` chặn).
- `ErrorCode(number, name, httpStatus, retryable, message)`: mã nghiệp vụ dùng HTTP **200**. Mỗi module một dải số riêng, không
  chồng dải đã dùng (core 0–999, Counter 10000–10099, Economy 11000–11099, IAP 12000–12099, GM 13000–13099, Identity 14000–14099,
  SampleGame 40000–40099, template 90000–90099). Tên viết `UPPER_SNAKE_CASE`.
- Aggregate mới: id dạng `[a-z][a-z0-9_]{1,31}`, lưu ở collection `pa_{id}`. `clientVisible: true` thì adapter **phải** đăng ký wire
  mapper (mục 4.5), nếu không host không khởi động.

### 4.3 `CommandDescriptor` và handler — logic

`modules/Sample.Counter/Sample.Counter/IncrementHandler.cs` (trích):

```csharp
public sealed class IncrementHandler(ICounterLimitPolicy policy) : ICommandHandler<IncrementCommand, IncrementResult>
{
    public const string Operation = "counter.increment";
    public const string LogKind = "counter_log";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [CounterAggregates.Counter]);

    public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context)
    {
        if (request.By <= 0)
        {
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Reject(CounterErrors.InvalidAmount));
        }

        if (request.By > policy.MaxPerIncrement)
        {
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Reject(CounterErrors.LimitExceeded));
        }

        var state = context.Get<CounterState>(CounterAggregates.Counter);
        var next = state with { Value = checked(state.Value + request.By), Increments = state.Increments + 1 };
        context.Set(CounterAggregates.Counter, next);
        context.AddOutbox(LogKind, new CounterLogEntry(request.By, request.Note, next.Value));
        return ValueTask.FromResult(CommandOutcome<IncrementResult>.Ok(new IncrementResult(next.Value)));
    }
}
```

Đọc đoạn này theo mô hình của bạn:
- `Descriptor`: `touches: [counter]` = "command này đọc và có thể ghi aggregate `counter`". Muốn chỉ đọc thêm aggregate khác thì
  thêm `reads: [...]`. Framework dựa vào đây để **get DB** trước khi gọi handler.
- `context.Get<CounterState>(...)` = dữ liệu đã được framework đọc sẵn (bản sao, sửa thoải mái).
- `context.Set(...)` = "save" **vào bộ đệm**; framework ghi thật sau khi handler trả `Ok`.
- `context.AddOutbox(...)` = hiệu ứng phụ commit cùng transaction.
- `Reject(...)` = không ghi gì.
- Policy `ICounterLimitPolicy` được tiêm qua constructor (thay được, mục 7).

Các khả năng khác của `ICommandContext` (`src/GameServer.Core/Commands/ICommandHandler.cs`): `Actor`, `Now` (giờ server cố định cho
lần thử), `Config`/`ConfigVersion` (game config), `TryClaimAsync(collection, key)` (hiệu ứng "chỉ một lần", cần khai
`maxClaimMarkers` trong descriptor), `NewId(purpose)` / `NewSeed(purpose)` (id và seed ngẫu nhiên ổn định qua các lần retry).
Extension: `ctx.AddAudit(...)` (GM, `src/GameServer.Core/Commands/Audit.cs`), `ctx.AddAnalyticsEvent(...)`
(`src/GameServer.Core/Analytics/AnalyticsEvents.cs`).

### 4.4 `ModuleDescriptor` — đăng ký command

`modules/Sample.Counter/Sample.Counter/CounterModuleDefinition.cs` (trích):

```csharp
public static ModuleDescriptor Describe() => new()
{
    Name = ModuleName,
    Version = "0.1.0",
    DependsOn = ["identity"],
    Permissions = [ReadPermission],
    // Log entries keep their history with the player id anonymized (PRIV-01, FR-019).
    Collections = [new CollectionSpec(LogCollection, Deletion: DeletionTreatment.Anonymize)],
    Aggregates = [CounterAggregates.CounterDescriptor],
    Commands = [Command.Define<IncrementCommand, IncrementResult>(IncrementHandler.Descriptor).HandledBy<IncrementHandler>()],
    ErrorCodes = CounterErrors.All,
    OutboxProjections = [new OutboxProjection(IncrementHandler.LogKind, LogCollection)],
};
```

- `Commands`: mỗi command một dòng `Command.Define<TReq, TRes>(descriptor).HandledBy<THandler>()`. Không có auto-discovery.
- Collection phụ (như `counter_log`) **bắt buộc** khai `Deletion:` (từ slice 010), nếu không host không khởi động.
- Handler **không** cần đăng ký DI; runner tạo nó bằng `ActivatorUtilities` mỗi lần thử, dependency trong constructor lấy từ DI.

### 4.5 Adapter: đăng ký DI, wire mapper, `MapPlayerCommand`, `ToDomain`

`modules/Sample.Counter/Sample.Counter.Adapters/CounterModule.cs` (trích):

```csharp
public sealed class CounterModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const int MaxNoteLength = 64;

    public ModuleDescriptor Describe() => CounterModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton<ICounterLimitPolicy, DefaultCounterLimitPolicy>();
        services.TryAddSingleton<ICounterService, CounterService>();
        services.AddAggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire);
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        routes.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            "/api/v1/counter/increment",
            IncrementHandler.Operation,
            ToDomain,
            result => new Wire.IncrementResult { Value = result.Value });
        // ... query /api/v1/counter/get (xem 4.9)
    }

    /// <summary>Wire → domain mapping and validation live in the adapter (ARC-02).</summary>
    private static IncrementCommand ToDomain(Wire.IncrementRequest request)
    {
        if (request.Note.Length > MaxNoteLength)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"note longer than {MaxNoteLength} characters.");
        }

        return new IncrementCommand(request.By, string.IsNullOrEmpty(request.Note) ? null : request.Note);
    }

    private static Wire.CounterState ToWire(CounterState state) => new() { Value = state.Value, Increments = state.Increments };
}
```

`MapPlayerCommand<TWireRequest, TRequest, TResult>(route, operation, toDomain, toWire)`
(`src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`):
- `route` phải có đoạn version `/v{n}/`.
- `operation` phải trùng tên trong `CommandDescriptor`.
- `toDomain`: **đọc param** + validate dạng/độ dài (lỗi → `BadRequest`). Rule nghiệp vụ (số âm, vượt hạn mức) để trong handler
  (trả mã nghiệp vụ).
- `toWire`: map result domain → message Protobuf.
- Tự gắn profile `Player`, gate, timeout, rate limiter.
- `TryAddSingleton` cho policy để game thay được bằng `ReplacePolicy`.

### 4.6 Đăng ký ở host

`samples/SampleHost/Program.cs`: `.AddModule<CounterModule>()`. Module phụ thuộc `identity` nên phải có `AddModule<IdentityModule>()`
(thiếu → báo lỗi khi khởi động). Project host cần `ProjectReference` tới `Sample.Counter.Adapters`
(`samples/SampleHost/SampleHost.csproj`) và hai project module nằm trong `GameServer.slnx`.

### 4.7 PCR

Cập nhật `docs/pcr/sample-counter.md` (hoặc `docs/pcr/<module>.md`): bảng aggregate, bảng **command → route, Touches, Reads, claim
marker, outbox**, bảng error code, capability thay được, cách xử lý khi xoá tài khoản.

### 4.8 Test

Test HTTP end-to-end dùng `TestHost` (`tests/Shared/TestHost.cs`, mặc định đăng ký `IdentityModule` + `CounterModule`, store
in-memory, xác thực thử bằng header). Trích `tests/GameServer.Platform.Web.Tests/CommandPipelineTests.cs`:

```csharp
[Fact]
public async Task Business_rejection_is_200_with_code_and_no_receipt()
{
    var response = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 11 }, ProtobufHttp.NewRequestId());

    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    var body = await response.ReadProtobufAsync<CommandResponse>();
    body.Code.ShouldBe("COUNTER_LIMIT_EXCEEDED");
    body.CodeNumber.ShouldBe(10001);
    body.ReceiptId.ShouldBeEmpty();
    body.Updates.ShouldBeEmpty();
    _host.Store.Count("receipts").ShouldBe(0);
}
```

Test mở rộng (thay policy/handler, service-only): `tests/Sample.Counter.Tests/ExtensionTests.cs`. Chạy:

```powershell
dotnet build GameServer.slnx -c Release
dotnet test --solution GameServer.slnx -c Release
```

Nên có tối thiểu: thành công + `updates`/`rev`; replay cùng requestId (`Replayed == true`, result giống hệt); mỗi mã từ chối; `ToDomain`
từ chối (400).

### 4.9 Query (đọc, không ghi) — để so sánh

Không phải command: không receipt, không transaction. `CounterModule` map `POST /api/v1/counter/get` bằng `MapPost(...).AsPlayerEndpoint()`
và đọc qua `PlayerSnapshotReader.ReadOneAsync` (`CounterService`). Dùng cho dữ liệu cần đọc mà không đổi; còn client thường đọc
section qua snapshot chung `POST /api/v1/player/snapshot`.

### 4.10 Gọi từ Unity

Code C# của message được sinh từ `.proto` bằng project `sdk/GameServer.Client/GameServer.Client.csproj`
(`dotnet build sdk/GameServer.Client -c Release`):
- contract của framework (`core.proto`, `identity.proto`, `economy.proto`, `iap.proto`) sinh vào gói
  `sdk/unity/com.gameserver.client/Runtime/Generated`;
- contract của game sinh vào **project Unity của game**, không vào gói. Ví dụ có sẵn (game mẫu):

```xml
<Protobuf Include="..\..\samples\SampleGame\SampleGame.Adapters\Protos\sample_game.proto" ProtoRoot="..\..\samples\SampleGame\SampleGame.Adapters\Protos" AdditionalImportDirs="..\..\proto" GrpcServices="None" OutputDir="..\..\samples\SampleGame\KestrelDemo\Assets\SampleGame\Generated" CompileOutputs="false" />
```

**Lưu ý:** `sample_counter.proto` hiện **chưa** được sinh cho Unity. Muốn gọi Counter từ KestrelDemo, thêm một dòng `<Protobuf>`
tương tự (trỏ `modules\Sample.Counter\Sample.Counter.Adapters\Protos\sample_counter.proto`, cùng `OutputDir`, `CompileOutputs="false"`),
build lại project trên, và preserve assembly chứa message trong `link.xml` của game (KestrelDemo: `Assets/link.xml`).

Gọi command — mẫu thật từ `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`:

```csharp
var buy = await client.SendCommandAsync(SampleGameRoutes.Buy, new BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 }, BuyItemResult.Parser);
log($"Buy → {buy.Code}");

var wallet = client.Sections.Get("wallet", WalletState.Parser);
```

Áp vào Counter (cùng API, sau khi đã sinh code):

```csharp
var inc = await client.SendCommandAsync("api/v1/counter/increment", new IncrementRequest { By = 1 }, IncrementResult.Parser);
if (inc.IsOk) { /* inc.Result.Value */ } else { /* inc.Code == "COUNTER_LIMIT_EXCEEDED" ... */ }
var counter = client.Sections.Get("counter", CounterState.Parser);   // rev: client.Sections.RevOf("counter")
```

Route trong SDK **không** có `/` đầu (ghép với `BaseUrl` kết thúc bằng `/`). `inc.Code` khác `"OK"` là mã nghiệp vụ; lỗi hạ tầng
(4xx/5xx sau khi hết retry) là `GameServerException`.

## 5. Ví dụ: thêm command thứ hai `counter.reset` (code mới cần viết)

Phần này là **code mới**, dựng hoàn toàn từ API có sẵn ở mục 4; dùng làm bài tập kiểm tài liệu (task T5.5). Thứ tự làm:

**(1) `.proto`** — thêm vào `sample_counter.proto` (số field mới, không đổi số cũ):

```proto
// POST /api/v1/counter/reset  (header X-Request-Id bắt buộc)
message ResetRequest {}

message ResetResult {
  int64 previous_value = 1;
}
```

**(2) Domain** — trong `CounterModel.cs`:

```csharp
public sealed record ResetCommand;

public sealed record ResetResult(long PreviousValue);
```

và mã lỗi mới trong `CounterErrors` (cùng dải 10000–10099, thêm vào `All`):

```csharp
public static readonly ErrorCode AlreadyZero = new(10003, "COUNTER_ALREADY_ZERO", 200, false, "The counter is already zero.");

public static ErrorCodeSet All { get; } = new(10000, 10099, LimitExceeded, InvalidAmount, AlreadyZero);
```

**(3) Handler** — file mới `ResetHandler.cs` trong `Sample.Counter`:

```csharp
public sealed class ResetHandler : ICommandHandler<ResetCommand, ResetResult>
{
    public const string Operation = "counter.reset";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [CounterAggregates.Counter]);

    public ValueTask<CommandOutcome<ResetResult>> HandleAsync(ResetCommand request, ICommandContext context)
    {
        var state = context.Get<CounterState>(CounterAggregates.Counter);
        if (state.Value == 0)
        {
            return ValueTask.FromResult(CommandOutcome<ResetResult>.Reject(CounterErrors.AlreadyZero));
        }

        context.Set(CounterAggregates.Counter, state with { Value = 0 });
        return ValueTask.FromResult(CommandOutcome<ResetResult>.Ok(new ResetResult(state.Value)));
    }
}
```

**(4) Đăng ký** — trong `CounterModuleDefinition.Describe()`:

```csharp
Commands =
[
    Command.Define<IncrementCommand, IncrementResult>(IncrementHandler.Descriptor).HandledBy<IncrementHandler>(),
    Command.Define<ResetCommand, ResetResult>(ResetHandler.Descriptor).HandledBy<ResetHandler>(),
],
```

**(5) Route** — trong `CounterModule.MapPlayerApi`:

```csharp
routes.MapPlayerCommand<Wire.ResetRequest, ResetCommand, ResetResult>(
    "/api/v1/counter/reset",
    ResetHandler.Operation,
    _ => new ResetCommand(),
    result => new Wire.ResetResult { PreviousValue = result.PreviousValue });
```

**(6) PCR** — thêm dòng `counter.reset` vào bảng command và mã `10003` vào bảng error code của `docs/pcr/sample-counter.md`.

**(7) Test** — theo mẫu `CommandPipelineTests`: increment 5 rồi reset → `Code == "OK"`, `ResetResult.PreviousValue == 5`,
`updates[0].NewRev == 2`; reset lần nữa → `COUNTER_ALREADY_ZERO`, không receipt; gửi lại cùng requestId của lần reset đầu →
`Replayed == true`.

**(8) Unity** — sinh lại code (mục 4.10) rồi `SendCommandAsync("api/v1/counter/reset", new ResetRequest(), ResetResult.Parser)`.

Không cần sửa Core, Platform, store hay migration DB: collection `pa_counter` đã có, state không đổi schema.

### Checklist thêm command

| # | Việc | File | Bị bắt ở đâu nếu quên |
| --- | --- | --- | --- |
| 1 | Message request/result trong `.proto` | `<Module>.Adapters/Protos/*.proto` | lỗi build |
| 2 | Record command/result (domain) | `<Module>/*.cs` | lỗi build |
| 3 | Error code mới trong dải của module, thêm vào `ErrorCodeSet` | `<Module>/*.cs` | trùng số/tên/dải, sai `UPPER_SNAKE_CASE` → không khởi động; **quên thêm vào set → không bị bắt** (xem mục 10) |
| 4 | Handler + `CommandDescriptor` (Touches/Reads đúng) | `<Module>/*Handler.cs` | aggregate không tồn tại → không khởi động; `Set` ngoài Touches → 500 lúc chạy |
| 5 | `Command.Define(...).HandledBy<...>()` trong `ModuleDescriptor.Commands` | `<Module>/*ModuleDefinition.cs` | quên → **404 `NOT_FOUND` lúc chạy** (không bị bắt khi khởi động) |
| 6 | `MapPlayerCommand` route `/api/v1/...` | `<Module>.Adapters/*Module.cs` | thiếu `/v{n}/` hoặc route trùng → không khởi động; quên → command không gọi được |
| 7 | Aggregate mới client-visible → `AddAggregateWireMapper` | `<Module>.Adapters/*Module.cs` | không khởi động |
| 8 | PCR | `docs/pcr/<module>.md` | review |
| 9 | Test | `tests/...` | review |
| 10 | Sinh code Unity | `sdk/GameServer.Client/GameServer.Client.csproj` | client không có type |

(Đối chiếu kiểm khi khởi động: `ModuleRegistry.Validate`, `src/GameServer.Platform/Registry/ModuleRegistry.cs`;
`EndpointSecurityValidator`, `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs`. Mọi lỗi của một lần khởi động được
báo cùng lúc, host thoát code 1.)

## 6. Bỏ hoặc tắt command

Framework **không có** switch cấu hình để tắt từng command. Các cách theo trường hợp:

| Muốn | Cách | Ghi chú |
| --- | --- | --- |
| Bỏ hẳn một command **trong module của mình** | Xoá dòng `Command.Define(...)` trong `ModuleDescriptor.Commands` **và** dòng `MapPlayerCommand` tương ứng; xoá handler/record; cập nhật PCR, test, code Unity | Giữ lại số field `.proto` đã dùng (đánh `reserved`) nếu sau này thêm message mới vào cùng file. Receipt cũ trong DB vô hại |
| Bỏ một module (vd. không dùng IAP) | Bỏ `AddModule<IapModule>()` ở host | Module khác `DependsOn` nó → lỗi khởi động chỉ rõ. Dữ liệu cũ của module vẫn nằm trong DB |
| Giữ service của module nhưng không mở endpoint nào | `AddModule<X>(m => m.ServiceOnly())` | Tắt **mọi** endpoint của module (ARC-04). Test: `Service_only_module_maps_no_endpoint_but_its_service_works` |
| Chặn một command của module framework mà không sửa module đó | `ConfigureExtensions(x => x.ReplaceHandler<TReq, TRes, THandler>(operation))` với handler luôn `Reject(<mã của game>)` | Endpoint vẫn tồn tại, client nhận mã nghiệp vụ; mã phải nằm trong dải error code của module game |
| Host chỉ chạy GM (hoặc chỉ Player) | `GameServer:Hosting:MapPlayerApi=false` / `MapGmApi=false` | Tắt cả nhóm API |
| Chặn một nhóm command với **một player** | Ban theo scope (GM), `BanCommandGuard` | Nhóm = phần trước dấu `.` của operation |

Cẩn thận: xoá đăng ký (`Commands`) nhưng quên xoá route → endpoint còn đó và **mọi lời gọi trả 404 `NOT_FOUND`**; host vẫn khởi động
bình thường.

## 7. Thay handler hoặc policy của module có sẵn

Game không sửa code của module framework; game đăng ký **override** sau khi `AddModule` module gốc
(`src/GameServer.Platform.Web/Hosting/ExtensionRegistry.cs`). Trích `tests/Sample.Counter.Tests/ExtensionTests.cs`:

```csharp
await using var host = await TestHost.StartAsync(b => b
    .AddModule<IdentityModule>()
    .AddModule<CounterModule>()
    .ConfigureExtensions(x => x.ReplacePolicy<ICounterLimitPolicy, GenerousPolicy>()));
```

```csharp
.ConfigureExtensions(x => x.ReplaceHandler<IncrementCommand, IncrementResult, DoublingHandler>(IncrementHandler.Operation)));
```

- `ReplacePolicy<TService, TImpl>()`: thay implementation đã đăng ký, giữ lifetime. Không có implementation mặc định → lỗi khởi động.
  Danh sách policy/service thay được của từng module: mục "Extensible capabilities" trong PCR (vd. `docs/pcr/sample-counter.md`,
  `docs/pcr/platform.md` mục 10).
- `ReplaceHandler<TReq, TRes, THandler>(operation)`: handler mới phải implement đúng `ICommandHandler<TReq, TRes>` (sai kiểu hoặc
  operation không tồn tại → lỗi khởi động). `CommandDescriptor` (Touches/Reads, profile) **giữ nguyên** của module gốc — handler mới
  chỉ được `Get`/`Set` các aggregate đó.
- Handler thay thế vẫn chạy trong pipeline chung: xác thực, gate, guard, idempotency, transaction. Test
  `Game_can_replace_the_handler_and_keeps_idempotency` chứng minh replay vẫn đúng.
- Gọi `ConfigureExtensions` **sau** `AddModule` của module gốc.

## 8. Command GM

Command do staff GM thực hiện trên một player dùng cùng runner (receipt, idempotency), khác ở:

| | Player command | GM command |
| --- | --- | --- |
| Map route | `MapPlayerCommand` | `MapGmCommand<TBody, TRequest, TResult>(pattern, operation, permission, toDomain)` (`src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs`) |
| Wire | Protobuf | JSON; body implement `IGmCommandBody` (có `Reason`, bắt buộc 1..500 ký tự) |
| Player đích | actor | route phải chứa `{playerId}` |
| Descriptor | profile mặc định `Player` | `profile: SecurityProfile.Gm, permission: "<permission>"`; permission phải khai trong `ModuleDescriptor.Permissions` |
| Audit | cấm | **bắt buộc đúng một** `context.AddAudit(...)` (runner kiểm, sai → 500) |
| Kiểm player đích | — | các `IGmTargetValidator` chạy trước (vd. player tồn tại, chưa xoá) |
| Response | `CommandResponse` | `GmCommandResponse` (JSON) |

Ví dụ thật — `samples/SampleGame/SampleGame/GmProgressionHandler.cs`:

```csharp
public static CommandDescriptor Descriptor { get; } = new(
    Operation,
    touches: [SampleGameAggregates.Progression],
    profile: SecurityProfile.Gm,
    permission: SampleGamePermissions.ProgressionEdit);
// ...
context.Set(SampleGameAggregates.Progression, after);
context.AddAudit(SampleGamePermissions.ProgressionEdit, Operation, request.Reason, before, after);
```

và route — `samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs`:

```csharp
routes.MapGmCommand<SetProgressionBody, SetProgressionCommand, SetProgressionResult>(
    GmProgressionRoute,                       // "/gm/v1/players/{playerId}/progression"
    SetProgressionHandler.Operation,
    SampleGamePermissions.ProgressionEdit,
    body => { /* validate */ return new SetProgressionCommand(body.Level, body.Xp, body.Stage, body.Reason!); });
```

Module map GM API trong `IGmApiModule.MapGmApi`. GM request cần `X-Request-Id` UUID như Player. Đổi GM API thì cập nhật
`docs/api/gm-openapi.json`: chạy test với `UPDATE_OPENAPI=1` (`tests/GameServer.Gm.Tests/OpenApiTests.cs`) rồi commit file. GM chỉ
**đọc** thì dùng `MapGet(...).AsGmEndpoint(permission)` như `CounterModule.MapGmApi`.

## 9. Các biến thể khác của `MapPlayerCommand`

| Biến thể | Dùng khi | Ví dụ |
| --- | --- | --- |
| `toDomainAsync: Func<TWire, HttpContext, Task<TRequest>>` | Cần gọi ra ngoài **trước** transaction (xác minh receipt IAP, token provider). Retry được replay trước khi gọi lại | `IapModule.VerifyAsync` (`src/GameServer.Modules.Iap.Adapters/IapModule.cs`) |
| `toDomainAsync` (tiếp) | | `identity.link` → `VerifyLinkAsync` (`src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`) |
| `afterExecute: Func<CommandExecution, HttpContext, Task>` | Làm mới cache của instance sau khi commit/replay (không chạy khi bị từ chối) | `identity.request_deletion` → `AfterDeletionRequestAsync` (`IdentityModule.cs`) |
| `MapDevCommand` | Cheat/công cụ dev; profile Dev, bị chặn ở Production | `CheatModule` trong `src/GameServer.Modules.Economy.Adapters/EconomyModule.cs` |
| `CommandOutcome.Commit(result, code)` | Ghi thay đổi nhưng báo một mã nghiệp vụ (vd. trận hết hạn được dọn) | `SampleGameErrors.BattleExpired` trong `samples/SampleGame/SampleGame/BattleHandlers.cs` |

## 10. Lỗi thường gặp

| Triệu chứng | Nguyên nhân | Sửa |
| --- | --- | --- |
| Build lỗi "Use IClock (TIME-01)" | Dùng `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `DateTime.Now`, `DateTime.Today`, `TimeZoneInfo.Local` (`BannedSymbols.txt`) | Trong handler dùng `ctx.Now`; nơi khác tiêm `IClock` |
| Build lỗi "Use await (PERF-01)" | `.Wait()`, `.Result`, `GetAwaiter().GetResult()` | `await` |
| 500 `INTERNAL`, log "wrote aggregate '…' which is not declared in Touches (DAT-09)" | `ctx.Set` aggregate không có trong `Touches` | Thêm vào `Touches` (tối đa 4) |
| 500, log "accessed aggregate '…' which is not declared in Touches or Reads (DAT-11)" | `ctx.Get` aggregate chưa khai | Thêm vào `Reads` (hoặc `Touches`) |
| Host không khởi động: "route has no version segment" | Route thiếu `/v{n}/` | `/api/v1/...`, `/gm/v1/...` |
| Host không khởi động: "command … references unknown aggregate" / "touches 0 aggregates" | Sai tên aggregate, `Touches` rỗng | Sửa descriptor |
| Host không khởi động: "error-code range … overlaps" | Dải error code trùng module khác | Chọn dải trống (xem 4.2) |
| Host không khởi động: "is client-visible but has no wire mapper" | Aggregate `clientVisible: true` thiếu `AddAggregateWireMapper` | Đăng ký trong `ConfigureServices` |
| Host không khởi động: "collection … declares no deletion treatment" | `CollectionSpec` thiếu `Deletion:` | Khai `Deletion: DeletionTreatment.NoPlayerData / Anonymize / Remove / ModuleStep` |
| Mọi lời gọi command trả 404 `NOT_FOUND` | Có route nhưng chưa thêm vào `ModuleDescriptor.Commands`, hoặc tên operation ở route khác descriptor | Dùng chung hằng `Operation` cho cả hai |
| 400 `BAD_REQUEST` dù body đúng | Thiếu `X-Request-Id` hoặc không phải UUID | SDK tự gửi; test dùng `ProtobufHttp.NewRequestId()` |
| 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` | Client dùng lại requestId cho body khác | Mỗi command mới một requestId; retry phải gửi đúng bytes cũ |
| 500, log "Result of '…' is N bytes; resultCompact max is 512" | Result quá lớn (lưu trong receipt) | Trả ít dữ liệu; dữ liệu lớn đi qua `updates` (section) |
| 500, log "Receipt of '…' is N bytes; max is 4096" | Outbox payload quá lớn | Thu gọn payload outbox |
| `AGGREGATE_TOO_LARGE` | Document aggregate > 64 KiB | Tách aggregate, dọn dữ liệu cũ |
| Hiệu ứng ngoài (HTTP, gửi mail…) chạy hai lần | Gọi trong handler; transaction chạy lại handler | Gọi trong `toDomainAsync` (trước) hoặc qua outbox consumer (sau) |
| Random/ID khác nhau giữa các lần retry | `Random`, `Guid.NewGuid()` trong handler | `ctx.NewSeed(purpose)`, `ctx.NewId(purpose)` |
| Thao tác "chỉ một lần" (nhận quà theo mã) bị nhân đôi giữa nhiều request khác requestId | Chỉ dựa vào receipt (theo requestId) | `ctx.TryClaimAsync(collection, key)` + `maxClaimMarkers` trong descriptor |
| GM command 500 "wrote 0 audit entries; expected 1" | Quên `ctx.AddAudit` | Gọi đúng một lần |
| `Commit(result, code)` nhưng client nhận `code = "OK"`; mã không có trong PCR/catalog | Tạo `ErrorCode` nhưng quên thêm vào `ErrorCodeSet` của module. Không bị bắt khi khởi động; runner tra mã đã commit trong catalog (`registry.Errors.FindByName`), không thấy thì coi là `OK` | Luôn thêm mã mới vào `ErrorCodeSet` (`ModuleDescriptor.ErrorCodes`) |
| Handler không tạo được (lỗi DI) | Dependency của constructor chưa đăng ký, hoặc đăng ký scoped | Đăng ký trong `ConfigureServices`, dạng singleton như `CounterModule` (runner tạo handler từ service provider gốc) |

## 11. Quy tắc viết handler (tóm tắt)

1. Chỉ dùng `ICommandContext` và dependency đã tiêm; không HTTP, không DB, không file.
2. Có thể bị gọi nhiều lần cho một request: không side effect ngoài `ctx`.
3. Chỉ `Set` aggregate trong `Touches`; chỉ `Get` aggregate trong `Touches`/`Reads`.
4. Giờ: `ctx.Now`. Random/ID: `ctx.NewSeed`/`ctx.NewId`.
5. Lỗi nghiệp vụ: `CommandOutcome.Reject(code)` (không ném exception). Lỗi dạng/độ dài input: trong `ToDomain`.
6. Hiệu ứng phụ: `ctx.AddOutbox` (+ `OutboxProjection` hoặc `IOutboxConsumer`); audit GM: `ctx.AddAudit`; analytics:
   `ctx.AddAnalyticsEvent`.
7. Kết quả nhỏ (≤ 512 B); dữ liệu lớn đi qua section.

Tài liệu liên quan: `docs/guides/module-authoring.md`, `templates/module/README.md`, `docs/pcr/platform.md` mục 2–3, 7, 10.
