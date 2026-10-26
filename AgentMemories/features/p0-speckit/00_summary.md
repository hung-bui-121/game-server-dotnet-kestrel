# RESUME — P0 qua Spec Kit (cập nhật 2026-10-26)

**Trạng thái:** Spec Kit 1.2.4 (`.claude/skills/speckit-*`), constitution v1.0.0. Owner duyệt spec+plan 001
(26/10) → chạy tự động tasks/implement cho các slice sau, không dừng.

| Slice | Nhánh | Trạng thái |
| --- | --- | --- |
| 001 platform-core | `001-platform-core` | DONE 88/88 task; review tech-lead (1 Blocker, 6 Major) đã sửa trên nhánh 002 (`801304a`) |
| 002 identity-session (guest) | `002-identity-session` (tách từ 001) | DONE 29/29 task (`16d8f5a`) |
| 003 SampleGame commands (economy, GetConfig, IAP verifier giả) | — | tiếp theo |
| 004 GM backend + frontend tối thiểu | — | |
| 005 observability/ops | — | |
| 006 Unity SDK | — | |

Test (26/10, cuối 002): 194 — 165 pass, 29 skip (integration Couchbase, thiếu `COUCHBASE_CONNSTR`), 0 fail.

**Quyết định owner 26/10/2026:** 6 slice tuần tự; không Docker/Couchbase trên máy → unit test bằng store
in-memory, integration/TST-04 viết sẵn và skip có lý do; chỉ dừng review sau spec+plan 001.

**Môi trường:** dùng `~/.dotnet10/dotnet.exe` (SDK 10.0.403; SDK hệ thống chỉ RC1, không có ASP.NET Core 10).
`global.json` bật runner Microsoft.Testing.Platform → `dotnet test --solution GameServer.slnx` / `--project`.
Heredoc trong Bash tool hay lỗi với ký tự đặc biệt → viết script Python ra scratchpad rồi chạy.

**Thiết kế đáng nhớ (chi tiết ở specs/*/research.md, docs/pcr/*.md, docs/adr/0001):**
- Runner DB-agnostic trên `ITransactionalDocumentStore`; `IKeyValueStore` cho collection ngoài transaction;
  `IPlayerAggregateWriter` cho epoch/purge (không receipt).
- `receiptId` = hash(actorId|playerId|operation|requestId); business rejection không receipt; guard chạy sau replay.
- Couchbase SDK 3.10: `GetMulti` không trả handle để replace → `UseGetMulti` mặc định **tắt** (đọc tuần tự) đến khi
  TST-04 V23-T1 đo trên cluster; chỉ tiêu "≈ 1 RTT" của DAT-11 chưa đạt — cần owner quyết sau khi đo.
- Insert race báo DocumentExists (không retry) → runner/writer tự chạy lại ≤ 3 vòng.
- Xác thực theo profile endpoint (SEC-02); test auth + in-memory chỉ Development/Test.
- Outbox: backoff 30 s×2^n ≤ 1 h, dead-letter sau 10 lần.

**Việc tiếp theo:** slice 003 trên nhánh mới tách từ `002-identity-session`.
