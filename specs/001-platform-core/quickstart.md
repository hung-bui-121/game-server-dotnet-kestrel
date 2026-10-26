# Quickstart / validation guide — 001 platform-core

## Prerequisites

- .NET SDK 10.0.403 (`global.json`). Trên máy dev hiện tại: `%USERPROFILE%\.dotnet10\dotnet.exe`
  (SDK hệ thống chỉ có 10.0 RC1, không có ASP.NET Core 10 runtime).
- Không cần Couchbase để chạy unit test. Integration test cần Couchbase ≥ 7.6, bucket replicas = 0
  (single node), transactions bật, và biến môi trường `COUCHBASE_CONNSTR`, `COUCHBASE_USERNAME`,
  `COUCHBASE_PASSWORD`, `COUCHBASE_BUCKET`.

## Build và test

```powershell
$dotnet = "$env:USERPROFILE\.dotnet10\dotnet.exe"
& $dotnet build GameServer.slnx -c Release          # warnings-as-errors
& $dotnet test GameServer.slnx -c Release            # unit + arch + web; integration skipped nếu thiếu env
```

Kỳ vọng: build sạch; mọi test unit/arch/web pass; test trong
`tests/GameServer.Platform.Couchbase.IntegrationTests` báo **Skipped** với lý do
`COUCHBASE_CONNSTR not set` (không Passed).

## Chạy host mẫu (store in-memory)

```powershell
& $dotnet run --project samples/SampleHost -- --GameServer:Store=InMemory
```

## Kịch bản kiểm chứng (đều có test tự động tương ứng)

| # | Kịch bản | Kỳ vọng | Spec / ACC |
| --- | --- | --- | --- |
| 1 | `POST /api/v1/counter/increment` (by=5, `X-Request-Id`=R) 3 lần, kể cả song song | 1 receipt, value = 5, 3 response cùng `receipt_id`/`new_rev`; lần 2–3 `replayed = true` | US1, ACC-04 |
| 2 | Cùng R, body khác | 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` | ACC-05 |
| 3 | Tiêm `AmbiguousAfterCommit` rồi retry R | lần 1: 503 `COMMIT_UNKNOWN_RETRY`; retry: kết quả đã commit, value không tăng thêm | ACC-07 |
| 4 | Dịch `IClock` +25 h, gửi lại R | xử lý như mới (counter tăng) — counter không có invariant một lần | ACC-42 (cơ chế) |
| 5 | Host thiếu profile / trùng route / trùng error code / thiếu `gameId` | process exit ≠ 0, message nêu module + mục | ACC-12, ACC-26 |
| 6 | Thay `ICounterLimitPolicy` | giới hạn mới có hiệu lực; Core không đổi | ACC-13 |
| 7 | Body 65 KiB / content type JSON / không header test auth / vượt limiter | 413 / 415 / 401 / 429 `ErrorEnvelope` | ACC-40 |
| 8 | `GET /v1/time`; mọi response Player | có `server_time_utc_ms` | ACC-43 |
| 9 | Increment làm counter state > 64 KiB (note dài lặp, test-only policy) | `AGGREGATE_TOO_LARGE`, không đổi state | ACC-39 |
| 10 | Document counter `schemaVersion = 1` | snapshot trả dạng v2; DB ghi lại ở increment kế tiếp | ACC-50 |
| 11 | Tắt relay inline, chờ sweeper | `counter_log::<entryId>` xuất hiện ≤ 60 s, không trùng | US7 |
| 12 | Snapshot `[]` và `[counter]` | đủ aggregate client-visible, `rev` đúng | ACC-34 |

## Integration (khi có Couchbase)

```powershell
$env:COUCHBASE_CONNSTR="couchbase://localhost"; $env:COUCHBASE_USERNAME="Administrator"
$env:COUCHBASE_PASSWORD="..."; $env:COUCHBASE_BUCKET="gameserver-dev"
& $dotnet test tests/GameServer.Platform.Couchbase.IntegrationTests -c Release
```

Kỳ vọng: store conformance test pass; 8 test TST-04 chạy và in kết quả để chép vào
`docs/pcr/platform.md` mục "TST-04 results".

## Validation log

2026-10-26 — SDK 10.0.403, Windows 11, no Couchbase:

- `dotnet build GameServer.slnx -c Release`: succeeded, 0 warnings (warnings-as-errors).
- `dotnet test --solution GameServer.slnx -c Release`: 137 tests — 120 passed, 17 skipped (`COUCHBASE_CONNSTR`
  not set: store conformance on Couchbase + 8 TST-04 experiments), 0 failed, ~3 s.
- Scenarios 1–12 covered by tests: `CommandRunnerTests`, `CommandPipelineTests`, `SnapshotTests`,
  `ErrorContractTests`, `StartupValidationTests`, `ExtensionTests`, `AggregateBudgetAndUpgradeTests`, `OutboxTests`.
- SampleHost on real Kestrel (`--urls http://127.0.0.1:5199`): `/v1/time` 200 protobuf; snapshot without auth
  401 protobuf; 70 KB body 413; snapshot 200; GM `/gm/v1/counter/k1` JSON.
- Not validated: anything requiring Couchbase (integration section above).

