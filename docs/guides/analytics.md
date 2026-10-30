# Analytics: login và payment (data team, game team)

Hướng dẫn đọc và dùng file analytics do server ghi (slice 012). Hợp đồng chính thức: PCR platform mục 18 (thay đổi là
breaking change). Vận hành (volume, shipper, tzdata, rollback, re-drive): `docs/guides/environments-and-local-run.md` mục 7.

## 1. Server ghi gì

| `event` | Ý nghĩa | Độ tin cậy | Khoá dedupe |
| --- | --- | --- | --- |
| `payment` | một purchase được store xác minh **và** server đã cấp quà | **ít nhất một lần**: không bao giờ mất, có thể có bản sao | `transactionID` |
| `login` | `source = login`: login cấp session mới (guest, Google Play Games, Game Center, switch đã xác nhận). `source = resume`: player quay lại bằng session đã lưu, request đầu tiên của ngày local trên một server instance | **best effort**: hiếm khi mất (quá tải, lỗi đĩa, crash) — server đếm số bị mất | `eventId` (hiếm khi cần) |

Không bao giờ có record cho: login bị từ chối hoặc thất bại, liên kết tài khoản, request mua gửi lại (cùng request id),
purchase bị từ chối (`ALREADY_GRANTED`, `INVALID_RECEIPT`, sản phẩm không có trong catalog), lỗi xác minh của store.

## 2. File

- Tên: `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl`, ví dụ `sample-pod-7f9c-payment-20261030.jsonl`. Một file cho mỗi server
  instance, mỗi loại event và mỗi **ngày UTC** của đồng hồ server **lúc ghi**. Tách tên từ bên phải: `event` không chứa `-`,
  `instanceId` có thể chứa.
- File có ngày UTC trước hôm nay là hoàn chỉnh, không bao giờ được ghi thêm; file của hôm nay đang được ghi thêm (có thể tail).
- Mỗi dòng là một object JSON (UTF-8), kết thúc bằng `\n`. **Bỏ qua mọi dòng không phải JSON hợp lệ**: đó là dòng bị cắt do
  server crash, có thể nằm **giữa** file sau khi server khởi động lại. Dòng đó chưa từng được xác nhận: một payment trong đó
  sẽ xuất hiện lại ở dòng sau; một login trong đó mất.
- **Không bao giờ lấy ngày nghiệp vụ từ tên file hay ngày của file.** Một payment có thể được instance khác ghi muộn, vào
  ngày UTC sau `chargeTime` (giao lại sau sự cố). Luôn dùng field thời gian trong record.
- `instanceId` không nhất thiết ổn định: trên Kubernetes Deployment (instance id mặc định = `HOSTNAME` = tên pod) mỗi lần pod
  restart là một `instanceId` mới, tức một bộ file mới. Đừng giả định một instance = một file mỗi ngày; gom theo `event` và
  ngày trong record. Vận hành (StatefulSet hoặc `Observability:InstanceId`, volume bền): `docs/guides/environments-and-local-run.md` mục 7.2.

## 3. Field chung (mọi record)

| Field | Kiểu | Ý nghĩa |
| --- | --- | --- |
| `event` | string | `payment` hoặc `login` |
| `schemaVersion` | int | `1` |
| `eventId` | string | 32 ký tự hex; **giống nhau ở mọi bản sao của cùng một payment**; khác nhau ở mỗi login/resume |
| `gameId` | string | game của deployment |
| `environmentId` | string | deployment: `local`, `dev`, `beta`, `uat`, `live` (game có thể có thêm). **Doanh thu: chỉ `live`** |
| `userID` | string | id player nội bộ. Sau khi tài khoản bị purge (≥ 30 ngày sau yêu cầu xoá), payment giao muộn mang id ẩn danh `anon_…` |
| `{time}` | string | giờ local của deployment kèm offset, ví dụ `2026-10-30T21:05:09.123+07:00` |
| `{time}Utc` | string | cùng thời điểm theo UTC, ví dụ `2026-10-30T14:05:09.123Z` |
| `timeZone` | string | múi giờ IANA của deployment, ví dụ `Asia/Ho_Chi_Minh`, `Asia/Taipei`, `Europe/London` (`UTC` chỉ ở dev/test) |
| `utcOffset` | string | offset **tại thời điểm đó** (`+07:00`; `Europe/London` là `+00:00` mùa đông, `+01:00` mùa hè) |

`{time}` là `chargeTime` với payment và `activeTime` với login.

### Ngữ nghĩa thời gian

- `{time}` và `{time}Utc` luôn chỉ **cùng một thời điểm**, chính xác tới millisecond (cắt bớt, không làm tròn).
- **`log_month` / `log_date`** = tháng / ngày của chuỗi `{time}` local: **7 / 10 ký tự đầu**. Đây là lịch local của deployment
  (theo yêu cầu owner). Muốn lịch UTC: lấy từ `{time}Utc`.
- Múi giờ là của deployment (một deployment = một thị trường), không phải của từng player.
- Ngày đổi giờ mùa hè (ví dụ `Europe/London`): offset đổi đúng tại thời điểm chuyển; giờ local có thể lặp lại một giờ vào mùa
  thu — dùng `{time}Utc` khi cần thứ tự tuyệt đối.

## 4. `payment` v1

| Field | Kiểu | Ý nghĩa |
| --- | --- | --- |
| `chargeTime`, `chargeTimeUtc` | string | **giờ server cấp quà** (commit của lệnh mua), không phải giờ mua của store. Purchase xác minh lúc 23:59:59 và cấp lúc 00:00:01 thuộc ngày (và tháng) sau |
| `receiptId` | string | receipt của server (truy vết) |
| `transactionID` | string | duy nhất cho mỗi purchase được cấp, có tiền tố store: `apple:{transactionId}`, `google:{orderId}` (`GPA.…`) hoặc `google:h:{32 hex}` (digest không đảo ngược được của purchase token khi Google không có order id: purchase test và một số promo) |
| `store` | string | `google`, `apple` |
| `productId` | string | product id của store; giá và tiền tệ **không** có trong record — tra theo store/catalog |
| `orderId` | string, tuỳ chọn | order id của store khi có |
| `isTest` | bool | `true` = purchase test/sandbox (Google license tester; Apple Sandbox/Xcode/LocalTesting) hoặc purchase giả ở môi trường dev. Luôn `false` trên `live` (purchase test bị từ chối ở đó) |
| `purchaseCategory` | string, tuỳ chọn | phân loại của Google khi có: `test`, `promo` (mã khuyến mãi: cấp hợp lệ nhưng **không có doanh thu**), `rewarded` |
| `storePurchaseTimeUtc` | string, tuỳ chọn | giờ mua do store báo, UTC |

### Dedupe

`dropDuplicates(["transactionID"])` (hoặc `eventId` — cùng kết quả). Các bản sao của một payment giống nhau ở mọi field
(`chargeTime`, `chargeTimeUtc`, `eventId`, `transactionID`, `receiptId`, …), **trừ** có thể `userID`: bản giao sau khi tài khoản bị
purge mang id ẩn danh. Giữ bản xuất hiện trước, hoặc bất kỳ bản nào.

### UL (số người trả tiền duy nhất mỗi tháng)

```sql
-- Spark SQL; payment = mọi dòng JSON hợp lệ có event = 'payment', đã dedupe: df.dropDuplicates(['transactionID'])
SELECT substr(chargeTime, 1, 7) AS log_month, COUNT(DISTINCT userID) AS ul
FROM payment
WHERE environmentId = 'live' AND NOT isTest AND COALESCE(purchaseCategory, '') <> 'promo'
GROUP BY substr(chargeTime, 1, 7)
```

Bỏ điều kiện `purchaseCategory <> 'promo'` nếu muốn đếm cả người nhận quà từ mã khuyến mãi.

## 5. `login` v1

| Field | Kiểu | Ý nghĩa |
| --- | --- | --- |
| `activeTime`, `activeTimeUtc` | string | giờ server của login, hoặc của request đầu tiên trong ngày local (resume) |
| `source` | string | `login` hoặc `resume` (player quay lại bằng session đã lưu, không login lại) |
| `provider` | string, chỉ khi `source = login` | cách đăng nhập: `guest`, `pgs` (Google Play Games), `gamecenter`, hoặc provider riêng của game |
| `isNewUser` | bool | login này tạo player (luôn `false` với `resume`) |

### Số ngày hoạt động mỗi player mỗi tháng (`active_days > 3`)

```sql
SELECT userID, substr(activeTime, 1, 7) AS log_month, COUNT(DISTINCT substr(activeTime, 1, 10)) AS active_days
FROM login
WHERE environmentId = 'live'
GROUP BY userID, substr(activeTime, 1, 7)
HAVING COUNT(DISTINCT substr(activeTime, 1, 10)) > 3
```

Nhiều record `login`/`resume` cho một player trong một ngày local là bình thường (nhiều instance, restart, login lại): **đếm
ngày phân biệt, không đếm record**. "Ngày" của `resume` là ngày local của deployment (`timeZone`).

## 6. Phiên bản schema

- `schemaVersion` theo từng event. Thêm field tuỳ chọn: giữ version — reader **phải bỏ qua field lạ**.
- Bỏ, đổi tên, đổi kiểu, định dạng hoặc ý nghĩa một field: version mới, báo trước ở PCR; trong giai đoạn chuyển tiếp cả hai
  version có thể nằm chung file (lọc theo `schemaVersion`).

## 7. Lưu giữ và quyền riêng tư (đầu vào cho privacy policy)

- `userID` là id player nội bộ, không băm. Không trực tiếp định danh người, nhưng là dữ liệu cá nhân theo privacy policy của
  game — policy phải nói tới file analytics.
- Dữ liệu đã ship giữ **25 tháng** (chính sách của kho dữ liệu; server không cưỡng chế). Xoá tài khoản **không** ghi lại file đã
  ghi hay đã ship.
- Record không bao giờ chứa: session token, mật khẩu, guest secret, token/auth code của provider, subject id của provider,
  purchase token Google, payload/JWS receipt, IP client, body request.

## 8. Game team

### Ghép host

```csharp
gameServer.AddObservability();      // log, OTel, /health
gameServer.AddAnalyticsFiles();     // file JSONL + readiness check "analytics" (mặc định của host mẫu)
```

`AddAnalyticsFiles()` idempotent (gọi hai lần = một lần). Không gọi nó (và không đăng ký sink nào khác) với
`GameServer:Analytics:Enabled=true` (mặc định): ở Staging/Production host **không khởi động** (exit 1, thông báo nêu cách sửa:
gọi `AddAnalyticsFiles()` hoặc đặt `GameServer:Analytics:Enabled=false`), vì các payment sẽ được ack mà không bao giờ có record;
ở Development/Test analytics inactive: một warning lúc khởi động, không ghi gì, entry payment vẫn được ack.

Cấu hình: section `GameServer:Analytics` (bảng đầy đủ ở PCR platform mục 18). Ở Staging/Production phải đặt
`GameServer:Analytics:TimeZone` (tên IANA); `GameServer:Analytics:Enabled` phải giống nhau trên mọi instance.

### Test

- `TestHost` đăng ký sẵn `InMemoryAnalyticsSink` (`TestHost.Analytics`: `Lines` — đúng các byte file sink ghi, `Records`,
  `OfEvent("payment")`, `FailWrites`, `WriteDelay`) và đặt `GameServer:Analytics:TimeZone` = `UTC`.
- `await host.FlushAnalyticsAsync()` chờ mọi record đã xếp hàng được ghi (login, resume là bất đồng bộ).
- Test chạy host mẫu bằng `WebApplicationFactory<Program>` phải đặt `GameServer:Analytics:Directory` vào thư mục tạm riêng và
  `Observability:InstanceId` riêng (hoặc tắt analytics), nếu không file sẽ được ghi vào thư mục mẫu và các class test chạy song
  song tranh nhau lock.

### Sink thứ hai (điểm mở rộng, preview)

Đăng ký thêm `IAnalyticsSink` (`Name`, `WriteAsync(batch, ct)`): sink nhận đúng các byte (một dòng JSON không có `\n`) như file
sink, theo thứ tự, theo batch ≤ 512. `WriteAsync` chỉ được trả về khi record đã giao xong; ném lỗi → batch tính là thất bại:
payment không được ack (receipt chờ sweeper), login bị mất và được đếm. Record có `Guaranteed = true` là payment (giao outbox):
batch chứa nó phải bền trước khi trả về (file sink flush xuống đĩa), vì receipt được ack ngay sau đó. Cái giá là một `fsync`
trên đường ack của purchase (đo trên máy dev: +58 ms p95; SC-004 sửa 2026-10-31 cho phép ≤ 100 ms). Đổi một dòng trong
`AnalyticsFileSink.WriteAsync` (`Flush(flushToDisk: durable)` → `false`) bỏ chi phí đó nhưng **đánh đổi độ bền khi node
crash/mất điện**: dòng payment đã ack có thể mất trong cửa sổ ghi trễ của OS và không bao giờ được giao lại.

### Event của game (điểm mở rộng, preview)

Một command của game có thể ghi event at-least-once của riêng nó chỉ bằng API Core:

```csharp
context.AddAnalyticsEvent(new AnalyticsEventType("tutorial_done", 1, "eventTime"), new { step = 3 });
```

Event có file riêng (`…-tutorial_done-…`), envelope chuẩn (`eventTime`/`eventTimeUtc`, `receiptId`, …) và field của game. Event
best effort từ adapter: `IAnalyticsEmitter.TryEmit`. Đây là **preview**: field của event game chưa phải hợp đồng công bố.
