# Tasks: Provider identities — Play Games / Game Center (P0.5 slice 009)

**Input**: [plan.md](plan.md), [spec.md](spec.md)

## Phase 1: Foundational (platform)

- [ ] T001 `ICommandContext.TryClaimAsync(collection, claimKey, attributes?)` lưu `attributes` trong claim marker; `CommandOutcome.Reject(code, details)` trong `src/GameServer.Core/Commands/ICommandHandler.cs`, `src/GameServer.Platform/Commands/CommandContext.cs`
- [ ] T002 Runner: rejection có HTTP ≠ 200 → `GameServerException` (details), không receipt; `ICommandRunner.TryReplayAsync` trong `src/GameServer.Platform/Commands/CommandRunner.cs`, `CommandModels.cs`
- [ ] T003 `MapPlayerCommand` async: replay receipt đã commit trước `toDomainAsync` trong `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`; test pipeline (non-200 reject có details, replay không gọi decode) trong `tests/GameServer.Platform.Tests` / `tests/GameServer.Platform.Web.Tests/CommandPipelineTests.cs`

## Phase 2: Identity contracts

- [ ] T004 `IIdentityProviderAdapter`, `IdentityCredential` (PlayGames/GameCenter/Custom), `VerifiedIdentity` (mapping key `{provider}:{subject}`, subject 1–128 in được không khoảng trắng), mã lỗi 14002–14004 trong `src/GameServer.Modules.Identity/ProviderIdentity.cs`
- [ ] T005 Proto `ProviderCredential`, `ProviderLoginRequest`, `LinkIdentityRequest/Result` trong `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`; `ProviderOptions` + validator (UseFake chỉ Dev/Test, PGS thiếu client id/secret, skew ≤ 10, cache ≤ 24) + `IdentityProviders` registry (trùng provider → startup fail)

## Phase 3: US1 — Link (P1)

- [ ] T006 [US1] `PlayGamesAdapter` (token exchange + players/me, 400/401 → proof invalid, 5xx/timeout → unavailable) trong `src/GameServer.Modules.Identity.Adapters/Providers/PlayGamesAdapter.cs`
- [ ] T007 [US1] `GameCenterAdapter` (URL host, tải ≤ 16 KiB, cache ≤ 24 h, chain pin DigiCert, subject Apple Inc., EKU code signing, skew, bundle, RSA-SHA256) trong `Providers/GameCenterAdapter.cs`; `FakeIdentityAdapter`
- [ ] T008 [US1] Handler `identity.link` (Touches profile, MaxClaimMarkers 1; alreadyLinked; PROVIDER_ALREADY_LINKED; IDENTITY_CONFLICT details provider) + endpoint `POST /api/v1/identity/link` (verify trong `toDomainAsync`)
- [ ] T009 [US1] Test `tests/GameServer.Platform.Web.Tests/GameCenterAdapterTests.cs` (chuỗi tự tạo: hợp lệ; chữ ký/salt/timestamp/bundle sai; skew; host lạ; chứng chỉ hết hạn; không chuỗi tới root; subject khác; cache) + chuỗi thật `gc-prod-12.cer` hợp lệ với pin mặc định; `PlayGamesAdapterTests.cs` (HTTP giả: OK, invalid_grant, 5xx, timeout)
- [ ] T010 [US1] Test `ProviderIdentityTests.cs`: link OK + mapping/identities/receipt; conflict 409 không đổi gì; race 2 instance một thắng (ACC-03); already linked; provider đã link; retry replay không verify lại; proof invalid không ghi
- [ ] T011 [P] [US1] Couchbase: hai player đua claim cùng key → đúng một commit trong `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseRunnerTests.cs`

## Phase 4: US2 + US3 — Login provider, switch (P1)

- [ ] T012 [US2] Tách `LoginCompletion` (checks → epoch + identity → session → snapshot) + `LoginRateLimits` dùng chung; `ProviderLoginService` (mapping lookup / tạo trong transaction, race → player thắng; phiên gửi kèm → `SWITCH_ACCOUNT`) + endpoint `POST /api/v1/identity/login/provider` (Public, gated)
- [ ] T013 [US2] Test: login provider sau cài lại → đúng player, phiên cũ bị thay (ACC-29); chưa map → player mới; race login lần đầu → cùng player; ban/xoá; lỗi xác minh tính vào giới hạn IP
- [ ] T014 [US3] Test switch: phiên A + identity của B → 409 `SWITCH_ACCOUNT` (`existing`), A vẫn sống; xác nhận → B; chưa map → `new`; phiên của chính B → login thường; token gửi kèm hỏng → bỏ qua (ACC-29)

## Phase 5: US4 — Adapter của game (P3)

- [ ] T015 [US4] Test adapter `custom` provider `acme` (login + link, mapping `acme:{subject}`); hai adapter trùng tên → startup fail

## Phase 6: SDK + polish

- [ ] T016 SDK `LoginWithProviderAsync(credential, switchAccount)` (RejectedOnly), `LinkProviderAsync(credential)`; regenerate proto; test `tests/GameServer.Client.Tests/ProviderClientTests.cs` với server thật (fake adapter): login, link, `SWITCH_ACCOUNT` giữ phiên, `IDENTITY_CONFLICT`
- [ ] T017 PCR `docs/pcr/identity.md` (login provider, link, mapping, cấu hình, mã lỗi), `platform.md` (claim attributes, non-200 reject, replay trước verify), `unity-sdk.md`; SampleHost appsettings (UseFake Development)
- [ ] T018 Toàn bộ test (kể cả Couchbase); tech-lead-reviewer; sửa; commit
