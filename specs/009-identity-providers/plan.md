# Implementation Plan: Provider identities — Play Games / Game Center (P0.5 slice 009)

**Branch**: `009-identity-providers` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

## Summary

Module identity: `IIdentityProviderAdapter` + adapter `pgs` (OAuth code exchange + `players/me`), `gamecenter` (chữ ký
`fetchItems`, chuỗi chứng chỉ pin), `fake` (Development/Test). Login provider (`/api/v1/identity/login/provider`) dùng
chung đuôi login guest (account checks → epoch → session → snapshot) và kiểm `SWITCH_ACCOUNT` theo phiên gửi kèm. Link =
player command `identity.link` dùng claim marker có attributes (insert mapping trong transaction của command). Platform:
`TryClaimAsync` nhận attributes; rejection có HTTP ≠ 200 trả envelope lỗi kèm details; command có bước xác minh async
replay receipt đã commit trước khi xác minh. SDK: `LoginWithProviderAsync`, `LinkProviderAsync`.

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403); SDK client C# 9 / netstandard2.1
**Primary Dependencies**: như 008 (không thêm package; `System.Security.Cryptography.X509Certificates`, `IHttpClientFactory`)
**Storage**: Couchbase / in-memory; collection `identities` (đã có)
**Testing**: xUnit v3 + Shouldly; `TestHost` 2 instance; chứng chỉ/chữ ký Game Center tự tạo; chứng chỉ thật
`gc-prod-12.cer` + chuỗi DigiCert làm fixture; HTTP giả cho Google; Couchbase test race claim
**Target Platform**: Linux server, Unity client
**Project Type**: web-service framework + client SDK
**Performance Goals**: login provider ≤ 2 lần gọi ra ngoài (PGS) / 0–1 (Game Center, cache chứng chỉ); timeout gọi ra
ngoài `Identity:Providers:TimeoutSeconds` (mặc định 10)
**Constraints**: không gọi ra ngoài trong transaction (IAP-01, CMD-03); không tin id client gửi; secret từ secret provider
**Scale/Scope**: 2 provider built-in + adapter của game

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | Link = command (một transaction: claim mapping + profile + receipt); xác minh trước transaction; login dùng `IPlayerAggregateWriter` như guest | PASS |
| II | Adapter đăng ký tường minh (DI), trùng provider → startup fail; command/error code trong descriptor | PASS |
| III | Login provider Public + gated; link Player; không nhận playerId từ client | PASS |
| IV | Timeout gọi ra ngoài, cache chứng chỉ có hạn (≤ 24 h), không sync-over-async | PASS |
| V | Proto identity mở rộng (field mới), mã lỗi 14002–14004, PCR identity/platform/unity-sdk | PASS |
| VI | Test xác minh (giả mạo), link/conflict/race/switch trên 2 instance, SDK, Couchbase race claim | PASS |
| VII | Metric `identity_link_total{provider,result}`, `login_total{result}`; log không chứa credential | PASS |

## Research / decisions

- **R1 Adapter**: `IIdentityProviderAdapter { string Provider; ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential,
  CancellationToken) }` trong `GameServer.Modules.Identity`; credential domain: `PlayGamesCredential(code)`,
  `GameCenterCredential(...)`, `CustomCredential(provider, data)`. `IdentityProviders` (registry) chọn adapter theo
  provider; trùng tên → lỗi khi khởi động. Lỗi xác minh → `IDENTITY_PROOF_INVALID`; lỗi mạng/5xx/timeout →
  `PROVIDER_UNAVAILABLE`.
- **R2 PGS v2 (V18, đã xác minh)**: POST `https://oauth2.googleapis.com/token` (`grant_type=authorization_code`, `code`,
  `client_id`, `client_secret`, `redirect_uri=""`) → `access_token`; GET `https://games.googleapis.com/games/v1/players/me`
  (Bearer). Key `pgs:{playerId}` (theo game, ổn định); `gamePlayerId`, `originalPlayerId` (chỉ có khi đã remap) vào
  attributes. 400/401 từ Google → proof invalid. Nguồn: developer.android.com/games/pgs/android/server-access,
  developers.google.com/games/services/web/api/rest/v1/players/get, next-gen player ids.
- **R3 Game Center (V19, đã xác minh)**: dữ liệu ký = `teamPlayerID` UTF-8 ‖ `bundleID` UTF-8 ‖ `timestamp` UInt64
  big-endian ‖ `salt`; RSASSA-PKCS1-v1_5 SHA-256. Chứng chỉ hiện tại `gc-prod-12.cer` (hạn 2027-05-27): subject
  `O=Apple Inc.`, EKU code signing, issuer DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1 → DigiCert Trusted
  Root G4 (SHA-256 `552F7BDC…C89988`), đã kiểm bằng `openssl verify`. Pin root + intermediate mặc định (cấu hình đè
  bằng `TrustedRootsBase64`/`IntermediatesBase64`); `X509ChainTrustMode.CustomRootTrust`, không tải AIA, không kiểm
  thu hồi. URL: https, host = `apple.com` hoặc đuôi `.apple.com`, không userinfo/port lạ; tải ≤ 16 KiB; cache theo URL ≤
  `CertificateCacheHours` (≤ 24). Nguồn: developer.apple.com fetchItems(forIdentityVerificationSignature:), forum 686122.
- **R4 Link**: `identity.link` (Touches `profile`, MaxClaimMarkers 1). Đã có key → OK `alreadyLinked`; có key khác cùng
  provider → `PROVIDER_ALREADY_LINKED`; `TryClaimAsync("identities", key, attributes)` false → reject
  `IDENTITY_CONFLICT` details `provider` (409, không receipt). Mapping link = claim marker
  `{claimKey, receiptId, playerId, gameId, environmentId, createdAtUtc, attributes}`; login đọc `playerId`.
- **R5 Rejection có HTTP ≠ 200**: `CommandOutcome.Reject(code, details)`; runner ném `GameServerException` (envelope
  đúng status + details) cho code HTTP ≠ 200, giữ `CommandResponse` cho code business (200).
- **R6 Replay trước xác minh**: `ICommandRunner.TryReplayAsync(operation, actor, requestId, bodyHash)`; overload
  `MapPlayerCommand` async gọi nó trước `toDomainAsync` (mã PGS dùng một lần; IAP bớt gọi store khi retry).
- **R7 Login provider**: Public + gated; mapping chưa có → `RunTransactionAsync` insert mapping (cùng cơ chế với claim
  của link, không trộn KV/transaction trên cùng key) → `DocumentExists` → đọc lại player thắng. Phiên gửi kèm
  (`Authorization: Bearer`) hợp lệ, player khác và `switch_account = false` → `SWITCH_ACCOUNT` (`account` = `existing` |
  `new`). Đuôi login (checks, epoch, identities, session, snapshot) tách thành `LoginCompletion` dùng chung với guest.
- **R8 Giới hạn**: `LoginRateLimits` dùng chung guest/provider: lỗi theo IP (verify thất bại tính lỗi), cảnh báo burst IP.
- **R9 Proto**: `ProviderCredential { oneof { PlayGamesCredential play_games = 1; GameCenterCredential game_center = 2;
  CustomCredential custom = 15; } }`; `ProviderLoginRequest { credential, switch_account }` → `LoginResponse`;
  `LinkIdentityRequest { credential }` → `LinkIdentityResult { provider, already_linked }`.
- **R10 Cấu hình**: `Identity:Providers:{UseFake, TimeoutSeconds, PlayGames{Enabled, ClientId, ClientSecret, TokenUri,
  PlayersMeUri}, GameCenter{Enabled, BundleIds[], MaxTimestampSkewMinutes ≤ 10, CertificateCacheHours ≤ 24,
  TrustedRootsBase64[], IntermediatesBase64[]}}`; `UseFake` ngoài Development/Test → startup fail; PGS bật thiếu
  client id/secret → startup fail.

## Structure

```text
src/GameServer.Core/Commands/ICommandHandler.cs          # TryClaimAsync attributes, Reject details
src/GameServer.Platform/Commands/{CommandContext,CommandRunner,CommandModels}.cs  # attributes, non-200 reject, TryReplayAsync
src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs                   # replay before async decode
src/GameServer.Modules.Identity/ProviderIdentity.cs      # adapter contract, credentials, VerifiedIdentity, link handler, errors
src/GameServer.Modules.Identity.Adapters/Providers/{PlayGamesAdapter,GameCenterAdapter,FakeIdentityAdapter,IdentityProviders,ProviderOptions}.cs
src/GameServer.Modules.Identity.Adapters/{LoginCompletion,ProviderLoginService,GuestLoginService,IdentityModule}.cs, Protos/identity.proto
sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs                 # LoginWithProviderAsync, LinkProviderAsync
tests/GameServer.Platform.Web.Tests/{ProviderIdentityTests,GameCenterAdapterTests,PlayGamesAdapterTests}.cs
tests/GameServer.Client.Tests/ProviderClientTests.cs
tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseRunnerTests.cs     # racing claim
docs/pcr/{identity,platform,unity-sdk}.md
```

## Complexity Tracking

Không có vi phạm. Mapping tạo ở login provider lần đầu ghi ngoài command pipeline (như guest ở P0, IDN-01 "tạo player
lần đầu = Insert mapping"), nhưng trong transaction để không trộn ghi KV và transaction trên cùng key với link.
