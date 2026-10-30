# game-server-dotnet-kestrel

Framework backend dùng chung cho nhiều game mobile: C# / .NET, ASP.NET Core + Kestrel, Couchbase.
Client Unity giao tiếp qua HTTPS + Protobuf; GM tool web qua HTTPS + JSON. Server giữ trạng thái
progression/resource chính thức (server-authoritative); client gửi action, server quyết định và trả kết quả.

## Trạng thái

P0 được làm theo Spec Kit, chia 6 slice (`specs/`). Đã xong:

- **001 platform-core** — host + module registry + startup validation, Player API Protobuf / GM API JSON, command
  runner một transaction với receipt/replay, outbox + sweeper, store Couchbase và in-memory, module mẫu `Sample.Counter`.
- **002 identity-session** — guest login, session một thiết bị (takeover), kiểm ban/xoá tài khoản.
- **003 sample-game** — Economy (wallet/inventory/ledger), IAP (verifier Apple/Google + fake), GetConfig, game mẫu
  (battle start/end, shop).
- **004 gm** — module GM: staff/grant/role template, cookie session, permission + scope game/env mỗi request,
  tra cứu player (PII theo permission), list phân trang keyset, điều chỉnh resource và progression có audit, trang GM tĩnh.

- **005 observability-ops** — log file JSON per instance (rolling, queue có giới hạn, che secret), OpenTelemetry (OTLP),
  `/health/live` + `/health/ready`, chi tiết health cho GM, graceful shutdown, ForwardedHeaders, kiểm nguồn secret.

- **006 unity-sdk** — gói UPM `com.gameserver.client`: transport Protobuf (`UnityWebRequest`), session, header gate, retry gửi lại
  đúng bytes, sự kiện session/tài khoản/gate, `rev` từng section, giờ server, config; `link.xml`, Google.Protobuf pin, sample client.

Đã chạy trên Couchbase EE 7.6.12 một node (2026-10-27): integration + TST-04, 2 instance, burst, tranh chấp, dừng êm, crash —
xem `docs/guides/local-real-env-testing.md`. Chưa kiểm: cluster nhiều node, collector OTLP, sandbox Apple/Google, thiết bị Android/iOS.

## Yêu cầu

- .NET SDK **10.0.403** (`global.json`). Trên máy chỉ có .NET 10 RC thì cài bản GA vào thư mục user:
  `dotnet-install.ps1 -Channel 10.0 -InstallDir $env:USERPROFILE\.dotnet10` rồi dùng `~/.dotnet10/dotnet`.
- Couchbase Server ≥ 7.6 chỉ cần cho integration test và "môi trường thật" (flavor `Dev`, `Beta`, `Uat`, `Live`); chạy local
  không cần.

## Chạy server local (một lệnh)

```powershell
./tools/local/run-local.ps1          # Windows PowerShell 5.1 / PowerShell 7
```

```bash
tools/local/run-local.sh             # macOS, Linux, Git Bash
```

Script build `samples/SampleHost`, chạy flavor `Local` (file store ở `samples/_database/dbbase`, không cần Couchbase), chờ server
sẵn sàng rồi in các URL:

| | URL |
| --- | --- |
| Player API | `http://localhost:5080/` (Unity client mẫu dùng mặc định địa chỉ này) |
| Health | `http://localhost:5080/health/ready` |
| GM tool | `http://localhost:5080/gm/v1/ui` — staff seed `admin` / `dev-only-change-me` (chỉ có ở flavor `Local` và `Dev`) |

Ctrl+C dừng êm; chạy lại giữ nguyên dữ liệu. `-Port 5090` đổi cổng, `-Reset` bắt đầu với store rỗng, `-Lan` cho điện thoại cùng
mạng kết nối (bash: `--port`, `--reset`, `--lan`). Từ IDE hoặc `dotnet run --project samples/SampleHost`: launch profile `local`
cho cùng kết quả (không in URL).

Flavor `Local`/`Dev`/`Beta`/`Uat`/`Live`, biến môi trường bắt buộc của từng flavor, file store, cấu hình Couchbase, TLS, quyền RBAC
và lệnh `--provision`: `docs/guides/environments-and-local-run.md`.

## Build và test

```powershell
dotnet build GameServer.slnx -c Release          # warnings-as-errors, analyzers bật
dotnet test --solution GameServer.slnx -c Release # unit + HTTP + arch; integration Skipped nếu thiếu COUCHBASE_CONNSTR
```

Log ghi ở `samples/SampleHost/logs/` (compact JSON); health: `/health/live`, `/health/ready`. Gửi trace/metrics tới
collector: đặt `Observability__Otlp__Endpoint=http://localhost:4317`.

Analytics (slice 012): host mẫu ghi một file JSONL cho mỗi instance, loại event (`login`, `payment`) và ngày UTC — flavor
`Local` ghi vào `samples/SampleHost/analytics/` (git bỏ qua). Field, thời gian, dedupe và truy vấn mẫu cho data team:
`docs/guides/analytics.md`; setting theo flavor (`GameServer:Analytics:TimeZone`, `GameServer:Analytics:Enabled`, …) và checklist
deploy: `docs/guides/environments-and-local-run.md` mục 7.

## Môi trường thật: Couchbase, nhiều instance

Flavor `Dev` chạy SampleHost trên Couchbase local (bucket `gameserver-dev`, mật khẩu trong biến môi trường user
`Couchbase__Password`): `dotnet run --project samples/SampleHost --launch-profile dev-couchbase`, hoặc nhiều instance
`./tools/local/start-hosts.ps1 -Count 2` (cổng 5080, 5081). Chuẩn bị Couchbase, tải và drill sự cố:
`docs/guides/local-real-env-testing.md`.

Integration test với Couchbase thật:

```powershell
$env:COUCHBASE_CONNSTR="couchbase://localhost"; $env:COUCHBASE_USERNAME="Administrator"
$env:COUCHBASE_PASSWORD="..."; $env:COUCHBASE_BUCKET="gameserver-it"   # bucket replicas = 0 nếu single node
dotnet test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release
```

## Cấu trúc

| Đường dẫn | Nội dung |
| --- | --- |
| `src/GameServer.Core` | contract: actor, clock, error code, descriptor, command handler |
| `src/GameServer.Platform` | registry, command runner, receipt, outbox, config, snapshot |
| `src/GameServer.Platform.Web` | hosting (`AddGameServer`/`UseGameServer`), Protobuf, lỗi, profile, limit |
| `src/GameServer.Platform.Couchbase` | store Couchbase (transactions, GetMulti, provisioning) |
| `src/GameServer.Platform.Observability` | Serilog file log, OpenTelemetry, health checks (`AddObservability()`) |
| `src/GameServer.Platform.Testing` | store in-memory + tiêm lỗi, `FakeClock`, file store của flavor local |
| `src/GameServer.Modules.Identity*` | aggregate bắt buộc `profile`, guest login, session |
| `src/GameServer.Modules.Economy*`, `src/GameServer.Modules.Iap*` | wallet/inventory/ledger; mua IAP |
| `src/GameServer.Modules.Gm*` | GM: staff, cookie session, lookup, audit, trang `/gm/v1/ui` |
| `samples/SampleGame`, `samples/SampleHost` | game mẫu và host chạy thử |
| `sdk/unity/com.gameserver.client` | gói Unity client SDK (UPM); `sdk/GameServer.Client` build cùng mã nguồn cho .NET |
| `samples/SampleGame/KestrelDemo` | client mẫu Unity 6 dùng SDK (scene `SampleGameClient`) |
| `sdk/unity/ci/smoke-build.ps1` | CI-02: build smoke Android IL2CPP + stripping High của KestrelDemo |
| `modules/Sample.Counter` | module mẫu (ACC-13) |
| `templates/module` | khung module 2 project |
| `docs/pcr`, `docs/adr`, `docs/guides` | Published Contract Register, ADR, hướng dẫn viết module, môi trường và chạy local, kiểm thử như môi trường thật |
| `tools/GameServer.LoadTest`, `tools/local` | công cụ tải/tranh chấp (ACC-06, ACC-16); script chạy local (`run-local`), chạy nhiều instance, drill dừng/crash/mất DB |
| `specs/` | Spec Kit: spec, plan, tasks theo slice |
