# Implementation Plan: Unity Client SDK (P0 slice 006)

**Branch**: `006-unity-sdk` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

## Summary

Một nguồn code duy nhất trong gói UPM `sdk/unity/com.gameserver.client/Runtime/`. Project `sdk/GameServer.Client/` (netstandard2.1,
C# 9) biên dịch chính các file đó và sinh code Protobuf vào `Runtime/Generated/` (commit, lockstep). Adapter Unity nằm trong
`#if UNITY_5_3_OR_NEWER` nên cùng file biên dịch được ở cả hai nơi. Test SDK chạy với host framework thật trên TestServer.

## Research / decisions

- **R1 Ngôn ngữ**: C# 9, không `record`/`init` (tránh phụ thuộc `IsExternalInit` trong Unity), nullable bật; `netstandard2.1`.
- **R2 Protobuf**: Google.Protobuf 3.36.2 (= server). Gói Unity mang `Plugins/Google.Protobuf.dll` (netstandard2.0) — V7: Unity
  2021.2+ có sẵn `System.Memory`/`Unsafe` trong profile .NET Standard 2.1.
- **R3 Codegen**: `Grpc.Tools` với `OutputDir` = `Runtime/Generated`, `CompileOutputs=false`; proto: core + identity + economy + iap.
  Proto của SampleGame sinh vào sample của gói.
- **R4 Thời gian**: offset = `serverMs − (sendMs + receiveMs)/2`; đồng hồ máy qua `IClientClock` (một chỗ dùng giờ hệ thống).
- **R5 Retry**: bytes body serialize một lần; mỗi lần gửi dùng lại mảng đó. Delay qua `IDelay` để test không phải chờ thật.
- **R6 Header gate**: hằng số `GateHeaders` ở server (Platform.Web) để hợp đồng có một nguồn; SDK dùng cùng tên.

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | SDK không quyết định nghiệp vụ; chỉ áp state server trả | PASS |
| V | Protobuf pin cùng version, contract từ `.proto` của server | PASS |
| VI | Test SDK với server thật trong process | PASS |

## Structure

```text
sdk/GameServer.Client/GameServer.Client.csproj         # build lõi SDK + codegen
sdk/unity/com.gameserver.client/                       # UPM: package.json, Runtime (asmdef, link.xml, Plugins, Generated), Samples~
tests/GameServer.Client.Tests/                         # SDK ↔ server tests
```
