# PCR — Unity client SDK

Gói UPM `com.gameserver.client` (slice 006, version 0.6.0 — lockstep với contract `.proto` của server). Nguồn: SDK-01..04,
API-09/10, IDN-10, GATE-02, TIME-01, CFG-03.

## Gói (semantic với client)

| Thành phần | Nội dung |
| --- | --- |
| `Runtime/Core` | `GameServerClient`, `SectionStore`, `GameServerError`, transport/clock/delay/session/config interfaces, `HttpClientTransport` |
| `Runtime/Unity` | `UnityWebRequestTransport` (chỉ biên dịch trong Unity, `#if UNITY_5_3_OR_NEWER`) |
| `Runtime/Generated` | code sinh từ `core.proto`, `identity.proto`, `economy.proto`, `iap.proto` (sinh lại khi build `sdk/GameServer.Client`) |
| `Runtime/Plugins/Google.Protobuf.dll` | 3.36.2 netstandard2.0 = version server pin (API-09); auto-referenced |
| `Runtime/Plugins/System.Runtime.CompilerServices.Unsafe.dll` | 4.5.2 netstandard2.0 — phụ thuộc của Google.Protobuf mà profile Unity không có trên Android (V7: thiếu thì IL2CPP build fail "Unable to resolve reference") |
| `Runtime/link.xml` + `Editor/LinkXmlProvider.cs` | preserve `Google.Protobuf` và `GameServer.Client`; provider đưa link.xml cho Unity linker (Unity không tự đọc link.xml trong package) |
| Client mẫu | **không** nằm trong gói: project Unity 6 `samples/SampleGame/KestrelDemo` tham chiếu gói bằng `file:` (login → config → battle → shop, log trên màn hình); code sinh từ `sample_game.proto` ở `Assets/SampleGame/Generated` |

Yêu cầu: Unity 2021.3+ (C# 9, .NET Standard 2.1). Code SDK giữ C# 9, không `record`/`init`. `.meta` được commit (cài qua git URL).

## Hành vi (semantic)

- **Header**: mọi request có `X-Request-Id` (UUID), `X-Client-Version`, `X-Platform`, `X-Build-Id` (tên = `GateHeaders` của server);
  request cần session có `Authorization: Bearer <token>`; token không bao giờ nằm trong body.
- **Command**: body serialize **một lần**, mọi lần retry gửi lại đúng mảng bytes đó cùng `X-Request-Id` → server replay, một hiệu ứng.
- **Retry** (mặc định 4 lần sau lần đầu): lỗi mạng/không có response, mã `retryable` (`COMMIT_UNKNOWN_RETRY`, `CONCURRENT_UPDATE`,
  `RATE_LIMITED`, `REQUEST_TIMEOUT`), 502/503/504 không có `ErrorEnvelope`. Chờ `max(200 ms × 2^n ≤ 5 s ± 20 %, retryAfterMs)`.
  Hết lượt → `GameServerException(NETWORK_ERROR | mã cuối)`.
- **Không retry**: mã nghiệp vụ (HTTP 200 + `code` ≠ OK → `CommandResult.Code`), lỗi 4xx, và các mã có sự kiện dưới đây.
- **Sự kiện**: `SessionReplaced` (xoá token; **không** tự đăng nhập lại), `AccountBlocked` (`ACCOUNT_SUSPENDED`, `ACCOUNT_DELETED` —
  xoá token, `SWITCH_ACCOUNT`), `Maintenance`, `UpdateRequired`, `ConfigVersionChanged(version)`.
- **Section** (API-10): login/snapshot ghi đè theo `rev` server; update `full` áp dụng nếu `newRev ≥ rev` cục bộ (không lùi);
  update không `full` chỉ áp khi `baseRev = rev` và `newRev = baseRev + 1`, nếu không SDK gọi snapshot cho đúng section đó.
- **Thời gian**: `ServerTimeOffset = serverTime − trung điểm(gửi, nhận)` từ mọi response có `server_time_utc_ms`; `ServerNow`.
- **Config**: `ServerConfigVersion` cập nhật từ mọi response; `RefreshConfigAsync` gọi `GetConfig` với version đang có, `not_modified` → không tải.

## Nghiệm thu

| Mục | Trạng thái |
| --- | --- |
| Hành vi SDK với server thật (TestServer): retry cùng bytes, COMMIT_UNKNOWN_RETRY, takeover, gate, config, rev | test tự động `tests/GameServer.Client.Tests` |
| Biên dịch trong Unity 2022.3.62f2 (SDK + Editor hook + sample) | đạt (batch mode, 26/10/2026) |
| CI-02 IL2CPP smoke build Android arm64, stripping High — Unity 2022.3.62f2 (project tạm) | **đạt** 26/10/2026 (APK 9,3 MB, 0 lỗi) sau khi thêm `System.Runtime.CompilerServices.Unsafe.dll` |
| CI-02 IL2CPP smoke build Android arm64, stripping High — Unity 6000.3.22f1 (KestrelDemo) | **đạt** 26/10/2026 (APK development 39 MB, 0 lỗi). E2E trong Unity 6 batch mode qua UnityWebRequest với SampleHost thật (`smoke-build.ps1 -E2E`): login, GetConfig, StartBattle/EndBattle OK, Buy trả mã nghiệp vụ, wallet rev 1 — **đạt** |
| Thiết bị thật Android + iOS (ACC-18) | **chưa** — thủ công: chạy SampleHost, mở KestrelDemo, đặt `serverUrl` trên object `SampleGameClient`, cài APK (`Build/KestrelDemo.apk`) lên máy cùng mạng, kiểm login/battle/shop/retry/config |
| iOS IL2CPP build | **chưa** — Unity 6 trên máy chưa cài module iOS; cần macOS + Xcode |

Chạy CI-02: `sdk/unity/ci/smoke-build.ps1 -Unity "<Unity 6>\Editor\Unity.exe"` (`-Setup` tạo lại scene demo, `-CompileOnly` chỉ biên dịch).
Project không được đang mở trong Editor; trên Windows giữ đường dẫn project ngắn (MAX_PATH).
