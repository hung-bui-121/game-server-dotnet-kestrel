# Kiểm thử như môi trường thật trên máy local

Chạy framework trên Couchbase thật (transactions bật), nhiều instance, có tải và có sự cố. Store in-memory của unit test
không mô phỏng được: ghi đè ngoài transaction, ATR/cleanup, độ trễ KV, hai process tranh cùng player, crash giữa commit.

Số đo trên một node localhost (RTT < 1 ms, replicas = 0) dùng để kiểm **tính đúng** và so sánh tương đối; số capacity
để ký duyệt hiệu năng phải đo trên cluster nhiều node giống production (bảng 12.1).

## 1. Chuẩn bị (một lần)

1. Couchbase Server Enterprise 7.6.x (bản dev/test miễn phí; Windows 10/11 hoặc Linux/WSL2). Bật service **Data, Query,
   Index**.
2. Hai bucket, **replicas = 0** (một node không đáp ứng được `Majority` khi replicas ≥ 1):
   `gameserver-it` (integration test, mỗi lần chạy tạo scope riêng rồi xoá) và `gameserver-dev` (SampleHost).
3. Mật khẩu Administrator đặt trong biến môi trường **user** `COUCHBASE_PW` — không ghi vào appsettings hay script
   (OPS-08; ngoài Development/Test host từ chối khởi động nếu secret nằm trong file cấu hình).

## 2. Integration test và TST-04

```powershell
$env:COUCHBASE_CONNSTR="couchbase://localhost"; $env:COUCHBASE_USERNAME="Administrator"
$env:COUCHBASE_PASSWORD=[Environment]::GetEnvironmentVariable('COUCHBASE_PW','User'); $env:COUCHBASE_BUCKET="gameserver-it"
& "$HOME\.dotnet10\dotnet.exe" test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release --output Detailed
```

Chép các dòng `[TST-04 …]` vào `docs/pcr/platform.md` mục 11 khi đổi phiên bản SDK hoặc Server.

## 3. Chạy nhiều instance trên Couchbase

```powershell
./tools/local/start-hosts.ps1 -Count 2          # http://localhost:5080/, http://localhost:5081/ — instance a, b
./tools/local/stop-hosts.ps1
```

- Host chạy từ bản copy trong `%TEMP%\gameserver-local\app` nên vẫn build/test được khi host đang chạy; PID và
  stdout/stderr cũng ở thư mục đó. Log ứng dụng: `samples/SampleHost/logs/sample-{a|b}-yyyyMMdd.log`.
- `-Otlp http://localhost:4317` gửi trace/metrics tới collector (vd. Jaeger v2 bản Windows, nhận OTLP ở 4317).
- Client Unity: `sdk/unity/ci/smoke-build.ps1 -Unity <Unity 6> -E2E -ServerUrl http://localhost:5080/`.

## 4. Công cụ tải `tools/GameServer.LoadTest`

Dùng bản .NET của Unity SDK (cùng Protobuf, header, requestId). SDK retry **tắt** (`--retries 0`) để đo đúng câu trả lời
của server. `GameServer.LoadTest.dll` nằm ở `tools/GameServer.LoadTest/bin/Release/net10.0/`.

| Kịch bản | Lệnh | Kiểm |
| --- | --- | --- |
| ACC-06 tranh chấp | `dotnet GameServer.LoadTest.dll contention --urls http://localhost:5080/,http://localhost:5081/ --rounds 20` | Hai `Buy` cùng lúc cho một player trên hai instance, gold đủ một lần: đúng một `OK` + một `INSUFFICIENT_FUNDS`; gold 0, potion +1; tổng ledger (đọc qua GM API) = wallet/inventory |
| ACC-16 burst | `dotnet GameServer.LoadTest.dll burst --urls …` | Mặc định 50 command/s + 20 login/s, burst ×3 trong 60 s (TBD-06); in timeline mỗi 5 s và bảng p50/p95/p99 theo phase; exit 1 nếu p95 command trong burst > 300 ms hoặc client bị bão hoà (arrival bị bỏ vì mọi player ảo đều bận, `--max-dropped`) |
| Sau mọi lần chạy | (tự động, `--verify-players 20`) | Ledger = wallet/inventory cho N player — phát hiện grant mất/trùng, receipt không có state |

Độ trễ tính từ **thời điểm arrival được lên lịch** (gồm cả thời gian chờ phía client — không bị coordinated omission).
Percentile tính mọi câu trả lời của server (kể cả 429/503) và timeout phía client (`CLIENT_TIMEOUT`, server treo);
chỉ loại `NETWORK_ERROR` (không kết nối được — instance đã dừng, local không có load balancer), được đếm riêng. Timeline
mỗi cửa sổ được in trễ một cửa sổ để các lời gọi bắt đầu trong đó kịp xong. GM dev seed (`admin`) dùng để đọc ledger;
mật khẩu lấy từ biến môi trường `GM_DEV_PASSWORD` (mặc định: seed Development), không nhận qua tham số dòng lệnh.
Tham số sai hoặc thiếu giá trị → báo lỗi, exit 2.

## 5. Drill sự cố

| Drill | Cách làm | Đạt khi |
| --- | --- | --- |
| ACC-28 dừng êm khi đang commit | Chạy `burst`, giữa chừng `./tools/local/send-ctrl-c.ps1 -ProcessId <pid host a>` (PID trong `%TEMP%\gameserver-local\hosts.json`) | `/health/ready` → 503 ngay; host vẫn phục vụ trong `DrainDelaySeconds` rồi thoát (≤ 30 s); log flush; verify ledger = wallet |
| Crash giữa commit | Như trên nhưng `Stop-Process -Id <pid> -Force` | Verify ledger = wallet (chờ tới 150 s cho cleanup/sweeper); không còn receipt `outboxPending = true` |
| ACC-16 mất DB rồi phục hồi | `./tools/local/couchbase-outage.ps1 -StopAfterSeconds 40 -DownSeconds 30` (một hộp thoại UAC: dừng service cần quyền admin) | Trong lúc mất: command trả nhanh `ErrorEnvelope` (503/504), `/health/ready` not-ready, không treo quá request timeout; sau khi Couchbase lên: tự phục hồi, verify ledger = wallet |

`send-ctrl-c.ps1` gửi **Ctrl+Break** (.NET coi là SIGQUIT, dừng host như SIGTERM trong container): process khởi động từ
automation kế thừa cờ "bỏ qua Ctrl+C" nên Ctrl+C không tới được.

Kiểm outbox còn treo (N1QL, Query Workbench hoặc REST cổng 8093):

```sql
SELECT COUNT(*) AS pending FROM `gameserver-dev`.g_sample.receipts WHERE outboxPending = true
```

## 6. Kết quả lần chạy đầu (2026-10-27, Windows, Couchbase EE 7.6.12 một node, 2 instance SampleHost)

| Mục | Kết quả |
| --- | --- |
| Integration (33 test, gồm TST-04) | 33/33 pass — chi tiết `docs/pcr/platform.md` mục 11 |
| Unity 6 E2E (KestrelDemo → SampleHost trên Couchbase) | Login, StartBattle, EndBattle OK; Buy `INSUFFICIENT_FUNDS` (đúng nghiệp vụ); receipt và ledger có trong Couchbase |
| ACC-06 | 20/20 vòng: `OK` + `INSUFFICIENT_FUNDS`, ledger khớp; SDK log "Attempt failed, attempting automatic rollback" = xung đột write-write được transaction xử lý |
| ACC-16 burst 150 command/s + 60 login/s | 100 % OK; p95 command 42–54 ms, p99 ≤ 180 ms |
| ACC-28 dừng êm giữa burst | Thoát sau ≈ 5,2 s (drain 5 s); verify 100/100; outbox pending 0 |
| Crash (kill -9) giữa burst | Verify 100/100; outbox pending 0 |
| Mất Couchbase | Chưa chạy (cần quyền admin) |

Phát hiện và đã sửa trong lần chạy này: Couchbase SDK ghi ~6 dòng Information mỗi transaction (~95 % log) → mặc định
`Observability:Logging:LevelOverrides` đặt `Couchbase.Client.Transactions` = Warning; provisioner tạo lại collection có sẵn làm SDK ghi Error
mỗi lần khởi động → chỉ tạo phần còn thiếu.
