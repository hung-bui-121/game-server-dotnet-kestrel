# Implementation Plan: SampleGame, Economy, Config, IAP (P0 slice 003)

**Branch**: `003-sample-game` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

## Summary

Thêm module framework `economy` (wallet/inventory, catalog, ledger, claim `extref`, cheat Dev), `iap` (verifier trước
transaction + grant một lần) và core endpoint `GetConfig`; module game `sample.game` (progression, battle, shop) chạy 5
command mẫu. Platform bổ sung `CommandOutcome.Commit(result, code)` và `ICommandContext.NewId`.

## Technical Context

Như 001/002. Thêm: `System.Security.Cryptography` (ES256, X509 chain) cho Apple JWS; `IHttpClientFactory` cho Google
(PERF-03). Không thêm package mới (Google OAuth service account ký JWT bằng RSA của BCL).

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | Mọi đổi resource trong transaction của command qua thư viện Economy; verify IAP trước transaction; handler thuần | PASS |
| II | 3 module mới đăng ký qua descriptor; SampleGame là module game, không sửa Core ngoài 2 API chung | PASS |
| III | Cheat profile Dev, chặn Production; GetConfig là Player | PASS |
| IV | HttpClient qua factory, timeout; verifier lỗi → 503 retryable | PASS |
| V | proto per module; error range per module; PCR economy/iap/sample-game | PASS |
| VI | Unit (runner harness) + HTTP + hai instance (ACC-53, ACC-06) | PASS |
| VII | Ledger là bản ghi bền; metric verify | PASS |

## Project Structure

```text
src/GameServer.Core/Commands/ICommandHandler.cs                  # + Commit(result, code), NewId
src/GameServer.Platform/Commands/CommandContext.cs, CommandRunner.cs
src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs           # + GetConfig
proto/gameserver/core/v1/core.proto                              # + GetConfigRequest/Response
src/GameServer.Modules.Economy/                                  # wallet, inventory, catalog, Economy ops, errors
src/GameServer.Modules.Economy.Adapters/                         # module, wire, cheat module, economy.proto
src/GameServer.Modules.Iap/                                      # verifier contract, catalog, purchase handler
src/GameServer.Modules.Iap.Adapters/                             # module, fake/apple/google verifiers, iap.proto
samples/SampleGame/SampleGame/                                   # progression, battle, shop, policies
samples/SampleGame/SampleGame.Adapters/                          # module, endpoints, sample_game.proto
samples/SampleHost/config/base/{resources,shop,battle,iap}.json
docs/pcr/{economy,iap,sample-game}.md
tests/SampleGame.Tests/                                          # unit (runner harness) + HTTP (ACC-53, ACC-06, …)
```
