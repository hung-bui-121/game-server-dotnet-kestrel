# Kiểm thử như môi trường thật trên máy local

Chạy framework trên Couchbase thật (transactions bật), nhiều instance, có tải và có sự cố. Store in-memory của unit test
không mô phỏng được: ghi đè ngoài transaction, ATR/cleanup, độ trễ KV, hai process tranh cùng player, crash giữa commit.

Số đo trên một node localhost (RTT < 1 ms, replicas = 0) dùng để kiểm **tính đúng** và so sánh tương đối; số capacity
để ký duyệt hiệu năng phải đo trên cluster nhiều node giống production (bảng 12.1).

Chỉ cần chạy server để code client/GM (không Couchbase): `tools/local/run-local.ps1` (flavor `Local`). Flavor, biến môi trường,
setting Couchbase, quyền RBAC và lệnh `--provision`: `docs/guides/environments-and-local-run.md`.

## 1. Chuẩn bị (một lần)

1. Couchbase Server Enterprise 7.6.x (bản dev/test miễn phí; Windows 10/11 hoặc Linux/WSL2). Bật service **Data, Query,
   Index**.
2. Hai bucket, **replicas = 0** (một node không đáp ứng được `Majority` khi replicas ≥ 1):
   `gameserver-it` (integration test, mỗi lần chạy tạo scope riêng rồi xoá) và `gameserver-dev` (SampleHost).
3. Mật khẩu Administrator đặt trong biến môi trường **user** `COUCHBASE_PW` — không ghi vào appsettings hay script
   (OPS-08; ngoài Development/Test host từ chối khởi động nếu secret nằm trong file cấu hình). Flavor `Dev` (launch profile
   `dev-couchbase`) đọc mật khẩu từ biến môi trường user `Couchbase__Password`.
4. Schema của `gameserver-dev`: flavor `Dev` tự tạo khi khởi động (Development provision theo mặc định). Muốn tạo trước, hoặc
   bổ sung phần còn thiếu sau khi đổi phiên bản (in từng mục rồi thoát 0/1/2, chạy lại an toàn):
   `dotnet run --project samples/SampleHost --launch-profile dev-couchbase -- --provision`.

## 2. Integration test và TST-04

```powershell
$env:COUCHBASE_CONNSTR="couchbase://localhost"; $env:COUCHBASE_USERNAME="Administrator"
$env:COUCHBASE_PASSWORD=[Environment]::GetEnvironmentVariable('COUCHBASE_PW','User'); $env:COUCHBASE_BUCKET="gameserver-it"
& "$HOME\.dotnet10\dotnet.exe" test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release --output Detailed
```

Chép các dòng `[TST-04 …]` vào `docs/pcr/platform.md` mục 11 khi đổi phiên bản SDK hoặc Server.

Mỗi class test tạo scope riêng `g_it…` / `g_prov…` (và user RBAC tạm `gs-it-…`) trong `gameserver-it` rồi xoá khi xong. Cả bộ
Couchbase mất ~16 phút; index service xếp hàng các lần build index nên test provisioning đặt timeout 1800 s.

**Dọn scope sót lại.** Lần chạy bị dừng giữa chừng (Ctrl+C, kill, ổ đầy) để lại scope test; Couchbase lưu dữ liệu trên ổ cài đặt
(thường là C:) nên vài scope sót có thể làm đầy ổ (đã gặp: 7 scope ≈ 9,6 GB), và ổ đầy lại làm lần chạy sau chết tiếp. Liệt kê
rồi xoá qua REST (không in mật khẩu):

```powershell
$pw = [Environment]::GetEnvironmentVariable('COUCHBASE_PW','User')
$auth = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("Administrator:$pw")) }
$base = 'http://localhost:8091/pools/default/buckets/gameserver-it/scopes'
$left = (Invoke-RestMethod -Headers $auth -Uri $base).scopes.name | Where-Object { $_ -like 'g_it*' -or $_ -like 'g_prov*' }
$left | ForEach-Object { Invoke-RestMethod -Headers $auth -Method Delete -Uri "$base/$_" }
```

User tạm sót lại: `GET http://localhost:8091/settings/rbac/users` rồi `DELETE /settings/rbac/users/local/<tên gs-it-…>`. Chỉ
chạy khi không có lần test nào đang chạy (scope của lần chạy đang dở cũng khớp mẫu).

## 3. Chạy nhiều instance trên Couchbase

```powershell
./tools/local/start-hosts.ps1 -Count 2          # http://localhost:5080/, http://localhost:5081/ — instance a, b
./tools/local/stop-hosts.ps1
```

- Mỗi host chạy flavor `Dev` (script đặt `ASPNETCORE_ENVIRONMENT=Dev` rồi khôi phục giá trị cũ khi xong); các biến
  `GameServer__Store`, `Couchbase__*` và `--urls` của script vẫn ghi đè `appsettings.Dev.json`.
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
