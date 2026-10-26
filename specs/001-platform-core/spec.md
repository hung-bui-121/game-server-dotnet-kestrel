# Feature Specification: Platform Core (P0 slice 001)

**Feature Branch**: `001-platform-core`

**Created**: 2026-10-26

**Status**: Draft

**Input**: User description: "001 platform-core — nền tảng P0 của Modular Game Server Framework (slice đầu tiên trong 6 slice của P0): host + đăng ký module tường minh + startup validation; Player API Protobuf / GM API JSON với ErrorEnvelope và error-code catalog; player aggregate registry; command pipeline + transaction runner + receipt/outbox/sweeper; adapter Couchbase và store in-memory; module mẫu Sample.Counter; TST-04. Không gồm identity/session thật, SampleGame 5 command, GM permission đầy đủ, Unity SDK."

Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4 (gọi tắt "tài liệu yêu cầu"). ID trong ngoặc là ID yêu cầu gốc.

## Bối cảnh và phạm vi slice

P0 được chia 6 slice làm tuần tự: **001 platform-core** → 002 identity/session (guest) →
003 SampleGame commands (economy, config `GetConfig`, IAP verifier giả) → 004 GM backend + frontend
tối thiểu → 005 observability/ops → 006 Unity SDK.

Slice 001 tạo "đường ống" mà mọi slice sau dựa vào: một game (host) khai báo module, module khai
báo player aggregate và command, client gửi command có `requestId`, framework commit đúng một lần
và trả các aggregate đã đổi. Người dùng của slice này là **lập trình viên game** (viết module trên
framework) và **client game** (gọi Player API); actor Player và GM ở slice này được xác thực bằng
cơ chế xác thực thử nghiệm (chỉ ở Development/test), được thay ở slice 002/004.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Command được commit đúng một lần, kể cả khi retry (Priority: P1)

Client gửi một command thay đổi trạng thái kèm `requestId`. Framework kiểm tra request, chạy
handler của module trên các aggregate mà command khai báo, rồi commit nguyên tử thay đổi aggregate
cùng receipt. Response trả mã kết quả và **chỉ các aggregate đã đổi** kèm `rev` mới. Nếu client mất
response và gửi lại đúng bytes với cùng `requestId`, nó nhận lại đúng kết quả cũ, không có hiệu ứng
thứ hai.

**Why this priority**: Đây là bảo đảm cốt lõi của framework (CMD-01..04, DAT-04, ACC-04). Mọi
command của SampleGame, GM và IAP ở các slice sau đều đi qua đường này.

**Independent Test**: Dùng module mẫu `Sample.Counter` (command `Increment`): gửi cùng command 3
lần, đồng thời và tuần tự; kiểm tra counter chỉ tăng một lần, có đúng một receipt, ba response giống
nhau về `resultCode`, `receiptId`, `newRev`.

**Acceptance Scenarios**:

1. **Given** player chưa có aggregate `counter` (mặc định, `rev = 0`), **When** gửi `Increment(by=5)`
   với `requestId` R, **Then** response thành công có một `SectionUpdate {id=counter, baseRev=0,
   newRev=1, full=true}` với giá trị 5, và tồn tại đúng một receipt của R (DAT-09, API-10).
2. **Given** command R đã commit, **When** gửi lại R cùng bytes, **Then** trả cùng `resultCode`,
   `receiptId`, `newRev` kèm aggregate ở trạng thái hiện hành (`full = true`, `rev ≥ newRev`); counter
   không đổi (CMD-02, CMD-04, ACC-04).
3. **Given** command R đã commit, **When** gửi R với body khác, **Then** 409
   `IDEMPOTENCY_PAYLOAD_MISMATCH`, không cập nhật (CMD-02, ACC-05).
4. **Given** hai actor khác nhau, **When** cả hai dùng cùng giá trị `requestId`, **Then** là hai command
   độc lập (CMD-01, ACC-05).
5. **Given** commit bị tiêm trạng thái "không rõ đã commit hay chưa", **When** client nhận
   503 `COMMIT_UNKNOWN_RETRY` và retry cùng `requestId`, **Then** framework tra receipt: có → trả kết
   quả cũ; không có → thực hiện như mới; luôn đúng một hiệu ứng (CMD-04, ACC-07).
6. **Given** hai instance nhận cùng `requestId` cùng lúc, **When** cả hai chạy, **Then** đúng một receipt,
   một hiệu ứng; bên thua trả cùng kết quả (CMD-03, CMD-05, ACC-44).
7. **Given** receipt của R đã quá replay window 24 h (đồng hồ server dịch ở Development), **When** gửi
   lại R, **Then** xử lý như request mới; command "một lần" bị invariant của module từ chối
   (CMD-07, ACC-42).

---

### User Story 2 - Lập trình viên game thêm module mà không sửa Core (Priority: P1)

Lập trình viên game viết một module mới: khai báo descriptor (tên, version, dependency, config bắt
buộc, permission, collection), player aggregate (id, kiểu state, schema version, client-visible, giá
trị mặc định), command (aggregate chạm/đọc, validator, handler), endpoint Player/GM và error code
riêng. Host đăng ký module bằng một lệnh. Game có thể thay policy hoặc handler mặc định bằng đăng ký
tường minh; module có thể được dùng như service mà không mở endpoint.

**Why this priority**: Khả năng mở rộng không fork Core là mục tiêu số 2 của framework (ARC-03,
ARC-04, EXT-01..05, PLY-06, ACC-13).

**Independent Test**: Thêm `Sample.Counter` vào host thử nghiệm; thay policy giới hạn tăng mặc định
bằng policy của game; dùng counter như service từ một module khác mà không map endpoint; so sánh
assembly Core trước/sau không đổi.

**Acceptance Scenarios**:

1. **Given** host không có `Sample.Counter`, **When** thêm một lệnh đăng ký module, **Then** route,
   command, aggregate, permission, error code của module có hiệu lực mà không sửa Core (ARC-03, ACC-13).
2. **Given** policy mặc định "mỗi lần tăng tối đa 10", **When** game đăng ký policy thay thế "tối đa
   100" sau framework module, **Then** command dùng policy mới (EXT-02).
3. **Given** module đăng ký ở chế độ chỉ service, **When** host khởi động, **Then** không có endpoint
   nào của module được map nhưng module khác gọi được service (ARC-04).
4. **Given** handler mới thay thế, **When** chạy command, **Then** handler vẫn nằm trong pipeline xác
   thực, authorization, gate, idempotency và transaction chung (EXT-04).

---

### User Story 3 - Cấu hình sai bị chặn khi khởi động (Priority: P1)

Host phải từ chối khởi động (trước khi nhận traffic) nếu cấu hình module sai, với thông điệp chỉ rõ
module, mục sai và cách sửa.

**Why this priority**: Deny-by-default là nguyên tắc bảo mật (SEC-08) và là cách duy nhất để module
mới không mở lỗ hổng ngầm (ACC-12, ACC-26).

**Independent Test**: Chạy host thử nghiệm với từng cấu hình sai, kiểm tra process dừng với mã lỗi
khác 0 và thông điệp đúng.

**Acceptance Scenarios**:

1. **Given** endpoint không khai security profile (`Player`, `GM`, `Public`, `Dev`), **When** khởi động,
   **Then** startup fail (API-02, SEC-08, ACC-12).
2. **Given** trùng route+method, trùng command handler, trùng error code, hoặc public endpoint ngoài
   allowlist, **When** khởi động, **Then** startup fail nêu rõ hai bên trùng (ARC-07, API-11, SEC-09).
3. **Given** module thiếu dependency module hoặc thiếu config bắt buộc, hoặc deployment config thiếu
   `gameId`/`environmentId`, **When** khởi động, **Then** startup fail với tên module, mục thiếu và
   cách khắc phục (ARC-06, ARC-10, ACC-26).
4. **Given** command khai > 4 aggregate chạm, game đăng ký > 16 aggregate, aggregate không tồn tại
   trong registry, hoặc thiếu aggregate bắt buộc `profile`, **When** khởi động, **Then** startup fail
   (DAT-09).
5. **Given** module có security profile `Dev` và deployment environment là Production, **When**
   khởi động, **Then** startup fail (OPS-09, ACC-32 — phần validator).

---

### User Story 4 - Client đọc snapshot và áp dụng thay đổi theo `rev` (Priority: P2)

Client lấy snapshot các aggregate client-visible của mình (toàn bộ hoặc theo danh sách), mỗi
aggregate có `rev`. Sau mỗi command, client nhận `SectionUpdate` của aggregate đã đổi và áp dụng theo
quy tắc `rev`; khi lệch thì lấy lại snapshot aggregate đó.

**Why this priority**: Đồng bộ client theo `rev` là contract của Player API (API-08, API-10,
ACC-34); Login của slice 002 sẽ dùng cùng snapshot reader.

**Independent Test**: Gọi snapshot trước/sau command; giả lập client giữ `rev` cũ rồi nhận
`SectionUpdate`; gọi snapshot theo một aggregate.

**Acceptance Scenarios**:

1. **Given** player mới, **When** gọi snapshot, **Then** mọi aggregate client-visible trả về với giá trị
   mặc định và `rev = 0`; aggregate không client-visible không xuất hiện (PLY-02, API-10).
2. **Given** command đổi 2 aggregate, **When** nhận response, **Then** có đúng 2 `SectionUpdate`, cả hai
   `newRev` cùng commit; aggregate không đổi không có trong response (API-10).
3. **Given** snapshot theo danh sách `[counter]`, **When** gọi, **Then** chỉ trả aggregate đó với `rev`
   đúng (ACC-34).

---

### User Story 5 - Lỗi nhất quán, có giới hạn và an toàn (Priority: P2)

Mọi lỗi trả về client có mã trong catalog chung, thông điệp an toàn, `requestId`, cờ `retryable` và
`retryAfter`, và thời gian server. Player API trả lỗi dạng Protobuf (kể cả lỗi do middleware sinh
ra); GM API trả Problem Details JSON. Request quá lớn, sai content type, quá tải bị từ chối có kiểm
soát.

**Why this priority**: Client SDK và GM tool phụ thuộc định dạng lỗi thống nhất (API-04, API-05,
API-11, ACC-40, ACC-43).

**Independent Test**: Gọi Player API với body quá 64 KiB, sai content type, không xác thực, vượt
limiter; gọi GM API sai; kiểm tra status, định dạng và trường bắt buộc.

**Acceptance Scenarios**:

1. **Given** Player request, **When** body > 64 KiB / sai content type / chưa xác thực / vượt
   concurrency limit, **Then** lần lượt 413 / 415 / 401 / 429 (+ `Retry-After`) với `ErrorEnvelope`
   Protobuf có `code`, `requestId`, `retryable`, `serverTimeUtc` (API-04, API-05, API-06, PERF-04, ACC-40).
2. **Given** GM request lỗi, **When** xử lý, **Then** trả Problem Details JSON với cùng `code` catalog
   (API-04).
3. **Given** bất kỳ response Player nào, **When** trả về, **Then** có `serverTimeUtc`; endpoint Public
   `/v1/time` trả thời gian server (TIME-01, ACC-43).
4. **Given** lỗi nghiệp vụ hợp lệ (ví dụ vượt giới hạn của counter), **When** trả về, **Then** HTTP 200
   với `code` nghiệp vụ, không đổi state, không receipt thành công (API-05).
5. **Given** exception không lường trước, **When** trả về, **Then** 500 với mã chung, không có stack
   trace hay secret (API-04).
6. **Given** client đưa `playerId`/`gameId` vào payload, **When** xử lý, **Then** bị bỏ qua; danh tính
   chỉ lấy từ actor context đã xác thực (API-12, ARC-10).

---

### User Story 6 - Giới hạn kích thước và nâng cấp schema của aggregate (Priority: P2)

Aggregate có ngân sách kích thước (cảnh báo 32 KiB, cứng 64 KiB) và `schemaVersion`. Document cũ
được nâng cấp lười khi đọc và ghi lại ở lần commit kế tiếp.

**Why this priority**: Bảo vệ hiệu năng và cho phép đổi schema không cần migration hàng loạt
(DAT-02, DAT-09, ACC-39, ACC-50).

**Independent Test**: Command làm aggregate vượt 32 KiB rồi 64 KiB; đọc document có
`schemaVersion` cũ.

**Acceptance Scenarios**:

1. **Given** command làm aggregate vượt 32 KiB, **When** commit, **Then** thành công và metric cảnh báo
   kích thước ghi nhận (ACC-39).
2. **Given** command làm aggregate vượt 64 KiB, **When** commit, **Then** `AGGREGATE_TOO_LARGE`, không
   aggregate nào trong Touches đổi, không receipt (DAT-09, ACC-39).
3. **Given** document `schemaVersion = 1` và upgrader 1→2 đã đăng ký, **When** đọc qua snapshot,
   **Then** client nhận dạng v2; document trong DB chỉ được ghi lại ở command kế tiếp; số document được
   nâng được ghi nhận (DAT-02, ACC-50).

---

### User Story 7 - Bản ghi phát sinh không bị mất (outbox) (Priority: P2)

Command có thể sinh bản ghi phụ (ledger, audit, log nghiệp vụ của module) như entry outbox trong
receipt, commit cùng state. Sau commit, framework chuyển entry sang collection đích ngay (best
effort); nếu thất bại, tác vụ quét định kỳ (30 s) chuyển bù. Mỗi entry chỉ xuất hiện một lần ở đích.

**Why this priority**: Ledger (slice 003) và audit GM (slice 004) dựa vào cơ chế này (JOB-02,
OPS-05, ACC-27 phần sweeper).

**Independent Test**: `Sample.Counter` sinh entry outbox `counter_log` mỗi lần tăng; tắt relay
ngay sau commit, kiểm tra sweeper tạo bản ghi đích trong ≤ 2 chu kỳ; chạy relay hai lần, kiểm tra
không trùng.

**Acceptance Scenarios**:

1. **Given** command sinh 2 entry outbox, **When** commit thành công và relay chạy, **Then** 2 bản ghi
   đích tồn tại, receipt đánh dấu không còn pending (JOB-02).
2. **Given** relay sau commit bị tắt/lỗi, **When** sweeper chạy, **Then** bản ghi đích được tạo và
   receipt hết pending (OPS-05).
3. **Given** relay và sweeper cùng xử lý một entry, **When** cả hai chạy, **Then** đích chỉ có một bản
   ghi theo `entryId` (idempotent).
4. **Given** command thất bại hoặc rollback, **When** kết thúc, **Then** không có entry outbox nào được
   relay.

---

### User Story 8 - Kiểm chứng giả định về transaction trên database thật (Priority: P3)

Đội backend chạy bộ test thực nghiệm TST-04 (8 test) trên database thật với phiên bản SDK đã pin để
xác nhận các hành vi mà tài liệu nhà cung cấp chỉ ghi "undefined". Khi môi trường không có kết nối
database, các test này được báo "skipped" kèm lý do, không được tính là pass.

**Why this priority**: TST-04 là điều kiện đóng P0 nhưng cần hạ tầng mà máy dev hiện tại chưa có;
code test phải sẵn sàng để chạy trong CI.

**Independent Test**: Chạy bộ test integration không có biến môi trường kết nối → 8 test skipped với
lý do; có kết nối → 8 test chạy và ghi kết quả.

**Acceptance Scenarios**:

1. **Given** không có cấu hình kết nối database, **When** chạy integration test, **Then** mọi test cần
   database báo skipped với lý do "không có kết nối" (constitution VI).
2. **Given** có kết nối database thật (transactions bật, `txn_meta` tồn tại), **When** chạy TST-04,
   **Then** 8 test chạy và kết quả được ghi vào PCR `platform.md` (TST-04).

---

### Edge Cases

- Command có `requestId` rỗng/không phải UUID → 400 với mã catalog, không chạy handler (API-07).
- Hai command khác `requestId` cùng player gửi đồng thời → serialize per player trong một instance
  (concurrency 1, queue 2; vượt queue → 429) và xuyên instance bằng conflict của transaction (CMD-06).
- Transaction hết hạn không ambiguous (conflict kéo dài) → 409 `CONCURRENT_UPDATE`, `retryable = true`
  (CMD-05).
- Client ngắt kết nối trong lúc commit → commit vẫn hoàn tất với deadline riêng, không dùng token
  của request (PERF-02).
- Handler ném exception → transaction rollback, không receipt, 500 an toàn.
- Handler cố ghi aggregate không khai trong Touches → bị từ chối (lỗi lập trình, 500) và không commit.
- Receipt hoặc `resultCompact` vượt 4 KiB / 512 B → lỗi lập trình được phát hiện, transaction
  rollback.
- Command thành công nhưng không đổi aggregate nào → vẫn có receipt; response không có
  `SectionUpdate`; không tăng `rev`.
- Document aggregate chưa tồn tại → trạng thái mặc định `rev = 0`, Insert ở lần ghi đầu (DAT-09).
- Nhận `requestId` cũ hơn replay window → như request mới (CMD-07).
- Database không xác nhận được mutation → không trả thành công giả, không ghi file local thay thế;
  trả `COMMIT_UNKNOWN_RETRY` (DAT-06).
- Body Protobuf đệ quy sâu/quá lớn sau decode → 400/413, không làm crash process (API-06).

## Requirements *(mandatory)*

### Functional Requirements

**Đăng ký module và khởi động**

- **FR-001**: Host MUST đăng ký mỗi module bằng một lệnh; module gom mọi đăng ký (service, config bắt
  buộc, permission, collection/index, player aggregate, command, error code, endpoint) qua một
  descriptor duy nhất (ARC-03, ARC-06).
- **FR-002**: Module MUST có thể đăng ký ở chế độ chỉ service (không map endpoint); host có thể chỉ
  map endpoint GM hoặc chỉ Player bằng cấu hình (ARC-04, OPS-10).
- **FR-003**: Mọi route, handler, policy, override MUST được đăng ký tường minh; không quét assembly
  (ARC-07).
- **FR-004**: Startup MUST fail trước khi nhận traffic khi: endpoint thiếu hoặc có nhiều hơn một
  security profile; trùng route+method; trùng command; trùng error code; public endpoint ngoài
  allowlist; thiếu dependency/config bắt buộc; thiếu `gameId`/`environmentId`; vi phạm giới hạn
  aggregate (≤ 16/game, ≤ 4 Touches, `profile` bắt buộc); module `Dev` ở Production. Thông điệp nêu
  module, mục vi phạm, cách sửa (API-02, ARC-06, ARC-07, ARC-10, DAT-09, OPS-09, SEC-08, SEC-09).
- **FR-005**: Nếu startup validator bị tắt, endpoint thiếu profile MUST bị từ chối khi gọi
  (deny-by-default) (ACC-12).
- **FR-006**: Game MUST thay được policy/handler mặc định bằng đăng ký tường minh sau framework
  module; thay thế không bỏ qua pipeline chung (EXT-02, EXT-04).
- **FR-007**: `gameId` và `environmentId` MUST lấy duy nhất từ deployment config và có trong actor
  context của mọi request (ARC-09, ARC-10).

**Giao thức Player/GM**

- **FR-010**: Player API MUST dùng Protobuf (`application/x-protobuf`), GM API dùng JSON; sai content
  type → 415 (API-01).
- **FR-011**: Mọi route MUST có prefix version `/v{n}` (API-02).
- **FR-012**: Lỗi Player MUST là `ErrorEnvelope` Protobuf (kể cả lỗi middleware 401/403/413/415/429/503);
  lỗi GM là Problem Details JSON; cả hai có `code` catalog, thông điệp an toàn, `requestId`,
  `retryable`, `retryAfter` khi retryable; không stack trace/secret (API-04).
- **FR-013**: HTTP status ánh xạ theo API-05; lỗi nghiệp vụ hợp lệ trả 200 với `code`.
- **FR-014**: Error-code catalog MUST là registry trung tâm có dải code per module, cờ `retryable`,
  mô tả an toàn; mã core gồm ít nhất: `UNAUTHENTICATED`, `FORBIDDEN`, `BAD_REQUEST`,
  `PAYLOAD_TOO_LARGE`, `UNSUPPORTED_MEDIA_TYPE`, `RATE_LIMITED`, `MAINTENANCE`, `UPDATE_REQUIRED`,
  `IDEMPOTENCY_PAYLOAD_MISMATCH`, `CONCURRENT_UPDATE`, `COMMIT_UNKNOWN_RETRY`,
  `AGGREGATE_TOO_LARGE`, `INTERNAL` (API-05, API-11).
- **FR-015**: Giới hạn MUST áp dụng: body Player ≤ 64 KiB, GM ≤ 1 MiB; timeout Player 10 s, GM 30 s;
  giới hạn đệ quy/kích thước khi decode Protobuf; concurrency limiter nhóm `command` 200 / `gm` 50
  per instance với queue = 2× permit, oldest-first; quá tải → 429/503 + `Retry-After` (API-06, PERF-04).
- **FR-016**: Mọi response Player MUST có `serverTimeUtc`; endpoint Public `/v1/time`; mọi module dùng
  đồng hồ server có thể offset ở Development (TIME-01, PLY-07).
- **FR-017**: Mỗi request MUST có correlation/request id xuyên log, receipt và lỗi.
- **FR-018**: Pipeline Player MUST có slot gate (maintenance, version tối thiểu) chạy trước xác thực
  và trước nghiệp vụ; slice 001 chỉ cần slot và mã lỗi, chưa cần dữ liệu gate (GATE-01, GATE-02 phần P0).
- **FR-019**: Danh tính, `gameId`, `environmentId`, quyền MUST lấy từ actor context; giá trị tương tự
  trong payload/query bị bỏ qua (API-12).
- **FR-020**: Slice 001 MUST cung cấp scheme xác thực thử nghiệm cho Player và GM, chỉ đăng ký được ở
  Development/test (validator chặn ở Production), để test pipeline trước khi có slice 002/004.

**Player aggregate**

- **FR-030**: Game MUST đăng ký player aggregate với: id, kiểu state, `schemaVersion`, client-visible,
  ngưỡng cảnh báo 32 KiB và cứng 64 KiB, factory giá trị mặc định, chuỗi upgrader; mỗi aggregate
  lưu ở collection `pa_<id>`, key = `playerId`; framework bắt buộc `profile` (DAT-09, DAT-01).
- **FR-031**: Mỗi document MUST có trường nền: id, `gameId`, `environmentId`, `schemaVersion`, `rev`,
  `createdAtUtc`, `updatedAtUtc` (DAT-10).
- **FR-032**: Document chưa tồn tại MUST được coi là trạng thái mặc định với `rev = 0` và được tạo ở
  lần ghi đầu (DAT-09).
- **FR-033**: `rev` MUST tăng đúng 1 cho mỗi aggregate thực sự đổi trong một command; aggregate
  không đổi giữ nguyên `rev`; `rev` không phải giá trị CAS của database (API-10).
- **FR-034**: Đọc document có `schemaVersion` cũ MUST nâng cấp lười trong bộ nhớ; ghi lại ở lần
  commit kế tiếp; không migration hàng loạt trong request; có bộ đếm số document được nâng (DAT-02).
- **FR-035**: Snapshot MUST trả mọi aggregate client-visible (hoặc danh sách yêu cầu) với `{id, rev,
  payload}`, đọc song song ngoài transaction, nhất quán per aggregate (PLY-02, API-10, DAT-11).
- **FR-036**: Aggregate ≥ 32 KiB MUST được ghi nhận bằng metric; > 64 KiB → `AGGREGATE_TOO_LARGE`,
  rollback toàn bộ (DAT-09).

**Command pipeline và transaction**

- **FR-040**: Command MUST khai báo descriptor: tên operation, aggregate chạm (Touches ≤ 4), aggregate
  đọc (Reads), số claim marker tối đa (≤ 2), validator, handler, security profile (DAT-09, PLY-06).
- **FR-041**: Command thay đổi trạng thái MUST nhận `requestId` (UUID) do client sinh; server dựng
  idempotency key (`gameId`, `environmentId`, `actorId`, operation, `requestId`) và `receiptId` = hash
  xác định của (`actorId`, operation, `requestId`) (API-07, CMD-01).
- **FR-042**: `bodyHash` MUST là SHA-256 trên raw request bytes như nhận được (CMD-02).
- **FR-043**: Pipeline MUST thứ tự: gate → xác thực → authorization → limiter per-player → decode +
  validate → tra receipt (replay) → transaction (đọc bundle aggregate, handler, ghi aggregate đổi,
  Insert receipt, Insert claim marker) → relay outbox → response (EXT-04).
- **FR-044**: Mọi command MUST commit trong một transaction gồm aggregate chạm + receipt (+ claim
  marker), durability `Majority`, thời hạn transaction 10 s, deadline commit tách khỏi request
  (DAT-04, DAT-05, PERF-02).
- **FR-045**: Aggregate của command MUST được nạp một lần theo bundle trong transaction, không tuần tự
  từng aggregate (DAT-11).
- **FR-046**: Handler MUST chỉ thao tác qua context của command (đọc/ghi aggregate đã khai, thêm claim
  marker, thêm outbox entry, đặt kết quả); không có side effect ngoài; có thể bị chạy lại (CMD-04).
- **FR-047**: Receipt MUST chứa: `receiptId`, `actorId`, operation, `requestId`, `bodyHash`,
  `resultCode`, `resultCompact` (≤ 512 B), `configVersion`, `schemaVersions`, `aggregates[{id,
  newRev}]`, `claimKeys[]`, `committedAtUtc`, `outbox[]`, `outboxPending`; ≤ 4 KiB; không expiry
  (CMD-03, CMD-08, DAT-03).
- **FR-048**: Retry trong replay window 24 h (theo `committedAtUtc`) cùng body MUST trả cùng
  `resultCode`, `receiptId`, `aggregates[].newRev`, `resultCompact` kèm aggregate hiện hành `full =
  true`; khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`; ngoài window → như request mới (CMD-02,
  CMD-07).
- **FR-049**: Commit ambiguous MUST trả 503 `COMMIT_UNKNOWN_RETRY` + `retryAfter`; transaction hết
  hạn do conflict → 409 `CONCURRENT_UPDATE` `retryable = true` (CMD-04, CMD-05).
- **FR-050**: Command cùng player MUST serialize trong một instance (concurrency 1, queue 2); player
  khác không chặn nhau (CMD-06).
- **FR-051**: Claim marker MUST là document insert-only khoá tự nhiên, Insert trong cùng transaction;
  trùng → handler nhận kết quả "đã tồn tại" để trả mã nghiệp vụ (ECO-08 dùng ở slice 003).
- **FR-052**: Mọi ghi vào `pa_*` và receipt MUST chỉ qua transaction runner (hoặc writer chuyên dụng
  cho login/purge); không đường ghi nào khác; có kiểm tra tự động (architecture test) (CMD-05, DAT-09).
- **FR-053**: Lookup receipt theo `receiptId` MUST có cho actor sở hữu (GM `player.read` ở slice 004),
  trả cả `outbox[]` (CMD-09).
- **FR-054**: Database không xác nhận mutation → không trả thành công, không ghi file local thay
  thế (DAT-06).

**Outbox**

- **FR-060**: Entry outbox MUST có `entryId` duy nhất, `kind`, payload; ghi trong receipt cùng
  transaction; module đăng ký consumer theo `kind` và collection đích (JOB-02).
- **FR-061**: Sau commit MUST relay inline best-effort; lỗi relay không làm command thất bại (PERF-01).
- **FR-062**: Sweeper MUST quét receipt `outboxPending = true` mỗi 30 s, relay idempotent theo
  `entryId`, đặt `outboxPending = false`; chạy an toàn trên nhiều instance (OPS-05).

**Persistence**

- **FR-070**: Phân vùng dữ liệu: environment = cluster/bucket, game = scope `g_<gameId>`, module =
  collection; collection/index (gồm `pa_<id>`, `receipts`, partial index `outboxPending`, `txn_meta`)
  được provisioning từ descriptor khi startup ở Development và bằng lệnh ops ở Production (DAT-01,
  DAT-07).
- **FR-071**: Mọi instance MUST dùng cùng collection metadata transaction `g_<gameId>.txn_meta`, tạo
  trước transaction đầu tiên; cleanup transaction mất bật trên mọi instance (DAT-07, OPS-05).
- **FR-072**: Kết nối database, collection và transaction MUST dùng singleton tái sử dụng (PERF-03).
- **FR-073**: Framework MUST có store in-memory hành xử giống store thật về: transaction nguyên tử,
  conflict, Insert trùng, đọc ngoài transaction không thấy dữ liệu chưa commit, tiêm lỗi
  ambiguous/expired — dùng cho unit test (constitution VI).
- **FR-074**: Game config dạng file: `config/base` + `config/env/<env>`, `manifest.json`; tính
  `configVersion = "{label}+{hash12}"` khi startup; thiếu/hỏng → startup fail; mọi receipt ghi
  `configVersion` (CFG-01, CFG-02). Phân phối `GetConfig` thuộc slice 003.

**Module mẫu và kiểm thử**

- **FR-080**: Framework MUST kèm module mẫu `Sample.Counter`: aggregate `counter` (client-visible),
  command `Increment(by)` với policy giới hạn thay được, query `GetCounter`, entry outbox
  `counter_log`, error code riêng (ACC-13, EXT-05).
- **FR-081**: Framework MUST kèm template module (2 project: nghiệp vụ + adapter) và hướng dẫn đăng
  ký/override (EXT-05, 4.2).
- **FR-082**: Integration test cần database MUST skip có lý do khi không có cấu hình kết nối; 8 test
  TST-04 có sẵn (TST-01, TST-04).
- **FR-083**: Architecture test MUST kiểm: Core/module nghiệp vụ không tham chiếu HTTP/database/wire
  type; không ghi `pa_*`/receipt ngoài runner; không `.Result`/`.Wait()`; không dùng đồng hồ hệ
  thống trực tiếp; mọi endpoint có profile; không Controllers (ARC-01, ARC-02, CMD-05, PERF-01,
  TIME-01, SEC-08).
- **FR-084**: PCR `docs/pcr/platform.md` MUST được tạo với limits, timeout, replay window, durability,
  error-code catalog core, transaction boundary chung (PCR-01).

### Key Entities

- **Module descriptor**: tên, version, dependency, config bắt buộc, permission catalog, collection/index,
  player aggregate, command, error code, upgrader của một module.
- **Player aggregate**: một document trạng thái nghiệp vụ của một player (ví dụ `profile`, `counter`);
  có `rev`, `schemaVersion`, trường nền; client-visible hay không.
- **Command descriptor**: operation, Touches, Reads, số claim marker tối đa, validator, handler, profile.
- **Receipt**: bằng chứng một command đã commit; chứa idempotency entry, kết quả rút gọn, rev mới,
  outbox; khoá `rcpt::{receiptId}`.
- **Outbox entry**: bản ghi phụ (`entryId`, `kind`, payload) đi kèm receipt, được relay sang collection đích.
- **Claim marker**: document insert-only khoá hiệu ứng "một lần".
- **Section / SectionUpdate**: biểu diễn aggregate trên wire: `{id, rev, payload}` /
  `{id, baseRev, newRev, payload, full}`.
- **ErrorEnvelope / error code**: định dạng lỗi chung và catalog mã lỗi.
- **Actor context**: `actorId`, loại actor (Player/GM/System), `gameId`, `environmentId`, quyền,
  correlation id.
- **Config version**: định danh tập file config hiệu lực.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% lần gửi lại cùng command (cùng `requestId`, cùng body) — tuần tự, đồng thời, sau
  commit không rõ — cho đúng một hiệu ứng và một receipt trong bộ test tự động (ACC-04, ACC-44).
- **SC-002**: Thêm `Sample.Counter` vào host và thay policy của nó không đổi một dòng nào trong Core
  (đo bằng diff Core = 0) (ACC-13).
- **SC-003**: 100% cấu hình sai thuộc danh sách FR-004 làm host dừng trước khi nhận request đầu tiên,
  với thông điệp nêu module và mục sai (ACC-12, ACC-26).
- **SC-004**: 100% response lỗi Player trong bộ test có đủ `code`, `requestId`, `retryable`,
  `serverTimeUtc`; 0 response chứa stack trace (ACC-40, ACC-43).
- **SC-005**: 0 trường hợp partial commit trong bộ test tiêm lỗi (aggregate + receipt luôn cùng có
  hoặc cùng không) (ACC-07).
- **SC-006**: Entry outbox bị bỏ lỡ bởi relay inline xuất hiện ở đích trong ≤ 60 s (2 chu kỳ sweeper),
  không trùng (JOB-02, OPS-05).
- **SC-007**: Bộ unit test chạy được trên máy không có database trong < 2 phút; integration test báo
  skipped có lý do khi thiếu kết nối.
- **SC-008**: Thời gian đọc aggregate của một command không tăng tuyến tính theo số aggregate (đọc 4
  aggregate ≈ thời gian đọc 1, đo ở integration test khi có database) (DAT-11).

## Assumptions

- Couchbase thật chưa có trên máy phát triển (owner chọn 26/10/2026): logic được kiểm bằng store
  in-memory; integration test và TST-04 viết sẵn, chạy khi có kết nối (CI hoặc máy có Docker).
- Xác thực thật (guest login, session, single-session takeover) thuộc slice 002; GM permission/scope
  đầy đủ thuộc slice 004. Slice 001 dùng scheme xác thực thử nghiệm chỉ cho Development/test.
- Ledger, audit là consumer outbox của slice 003/004; slice 001 chỉ cung cấp cơ chế và consumer mẫu
  `counter_log`.
- Observability đầy đủ (OTel export, log file rolling, health readiness chi tiết, metric đầy đủ OBS-01)
  thuộc slice 005; slice 001 chỉ phát metric/trace cơ bản cần cho tiêu chí của nó (kích thước
  aggregate, số lần upgrade, txn attempts).
- Rate limit login (SEC-13) thuộc slice 002; budget theo nhóm (PERF-05) là P0.5.
- Giá trị số lấy từ Phụ lục B của tài liệu yêu cầu; đều là giá trị khởi điểm đã chốt.
- Leaderboard/League ngoài phạm vi framework.
