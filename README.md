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

Chưa kiểm trên hạ tầng thật: Couchbase (TST-04), collector OTLP, sandbox Apple/Google, thiết bị Android/iOS và Unity LTS mới nhất.

## Yêu cầu

- .NET SDK **10.0.403** (`global.json`). Trên máy chỉ có .NET 10 RC thì cài bản GA vào thư mục user:
  `dotnet-install.ps1 -Channel 10.0 -InstallDir $env:USERPROFILE\.dotnet10` rồi dùng `~/.dotnet10/dotnet`.
- Couchbase Server ≥ 7.6 chỉ cần cho integration test và chạy thật (`Store=Couchbase`).

## Build, test, chạy

```powershell
dotnet build GameServer.slnx -c Release          # warnings-as-errors, analyzers bật
dotnet test --solution GameServer.slnx -c Release # unit + HTTP + arch; integration Skipped nếu thiếu COUCHBASE_CONNSTR
dotnet run --project samples/SampleHost          # store in-memory, xác thực thử nghiệm qua header X-Test-Player
```

Log ghi ở `samples/SampleHost/logs/` (compact JSON); health: `/health/live`, `/health/ready`. Gửi trace/metrics tới
collector: đặt `Observability__Otlp__Endpoint=http://localhost:4317`.

GM tool khi chạy SampleHost (Development): mở `http://localhost:5000/gm/v1/ui`, đăng nhập staff seed trong
`samples/SampleHost/appsettings.json` (`Gm:Bootstrap:Staff`, chỉ hợp lệ ở Development/Test).

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
| `src/GameServer.Platform.Testing` | store in-memory + tiêm lỗi, `FakeClock` |
| `src/GameServer.Modules.Identity*` | aggregate bắt buộc `profile`, guest login, session |
| `src/GameServer.Modules.Economy*`, `src/GameServer.Modules.Iap*` | wallet/inventory/ledger; mua IAP |
| `src/GameServer.Modules.Gm*` | GM: staff, cookie session, lookup, audit, trang `/gm/v1/ui` |
| `samples/SampleGame`, `samples/SampleHost` | game mẫu và host chạy thử |
| `sdk/unity/com.gameserver.client` | gói Unity client SDK (UPM); `sdk/GameServer.Client` build cùng mã nguồn cho .NET |
| `samples/SampleGame/KestrelDemo` | client mẫu Unity 6 dùng SDK (scene `SampleGameClient`) |
| `sdk/unity/ci/smoke-build.ps1` | CI-02: build smoke Android IL2CPP + stripping High của KestrelDemo |
| `modules/Sample.Counter` | module mẫu (ACC-13) |
| `templates/module` | khung module 2 project |
| `docs/pcr`, `docs/adr`, `docs/guides` | Published Contract Register, ADR, hướng dẫn viết module |
| `specs/` | Spec Kit: spec, plan, tasks theo slice |
