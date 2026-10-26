# Modular Game Server Framework Constitution

## Core Principles

### I. Server authoritative, một transaction cho mỗi command

- Progression và resource do server quyết định; client chỉ gửi action/kết quả cần kiểm tra.
- Mọi command thay đổi trạng thái MUST chạy trong đúng một Couchbase transaction gồm các
  aggregate khai trong `CommandDescriptor.Touches` (≤ 4), receipt `rcpt::{receiptId}` (Insert)
  và claim marker (Insert, ≤ 2) (DAT-04, DAT-09, CMD-03).
- Mọi ghi vào document `pa_*` và `rcpt::*` MUST đi qua transaction runner. Không có fast path
  CAS thường, không ngoại lệ (V15 đã xác minh: trộn ghi là undefined).
- Ledger/audit là entry outbox trong receipt, relay inline + sweeper (JOB-02).
- Handler MUST thuần qua `ICommandContext`: không HTTP, không side effect ngoài trong lambda;
  verify bên ngoài (IAP) chạy trước transaction.

Lý do: idempotency và tính "một lần" chỉ đúng khi receipt và state commit nguyên tử.

### II. Module đăng ký tường minh

- Module vào hệ thống qua `IGameModule` + `ModuleDescriptor`; player state qua
  `PlayerAggregateDescriptor`; command qua `CommandDescriptor` + `ICommandRegistry`
  (ARC-03, PLY-06).
- Không auto-discovery theo assembly, không Controllers/Application Parts; dùng Minimal API
  map tường minh (ARC-07).
- Duplicate route+method, command handler, error code hoặc binding thiếu MUST làm startup fail
  trước khi phục vụ traffic.
- Thêm module hoặc thay policy MUST NOT cần sửa Core (EXT-01..EXT-05).

Lý do: framework dùng cho nhiều game; hành vi phải đoán trước được từ code đăng ký.

### III. Security deny-by-default

- Mọi endpoint MUST khai security profile (`Public`, `Player`, `Gm`…); endpoint thiếu profile
  làm startup fail (SEC-08).
- GM authorization MUST kiểm permission và scope `gameId`/`environmentId` tại backend trên mọi
  request; module chỉ kiểm permission, không kiểm tên role (SEC-07, SEC-15).
- Mọi mutation do GM thực hiện MUST đi qua pipeline command và để lại audit đủ 7 trường
  trong cùng transaction (SEC-14).
- Không shared API key nhúng trong client làm bằng chứng nhận diện (SEC-16).

### IV. Async và tải có giới hạn

- I/O (request body, Couchbase, outbound HTTP) MUST async; cấm `.Result`/`.Wait()`
  (analyzer/arch test) (PERF-01).
- Không fire-and-forget cho mutation authoritative; relay best-effort chỉ cho outbox đã commit.
- Mọi limiter, queue, timeout, payload limit MUST có giá trị số (Phụ lục B), ghi trong PCR.

### V. Contract ổn định

- Player API dùng Protobuf (Unity), GM API dùng JSON; lỗi trả `ErrorEnvelope` với mã trong
  error-code catalog (API-11); route có version.
- Published Contract Register (`docs/pcr/`) là nơi công bố limits, section/rev, consistency,
  transaction boundary, error code (PCR-01). Thay đổi semantic là breaking change (ARC-08).
- `Google.Protobuf` và Couchbase SDK MUST pin version.

### VI. Kiểm thử nhiều tầng

- Unit test với store in-memory MUST chạy được ở mọi máy, không cần hạ tầng.
- Integration test với Couchbase thật (transactions bật) MUST chạy trong CI; ở local chỉ chạy
  khi có biến môi trường kết nối, nếu không thì skip có thông báo — không được giả là pass.
- Architecture test MUST kiểm các luật ở nguyên tắc I–IV (không ghi `pa_*` ngoài runner,
  không sync-over-async, endpoint có security profile, không Controllers).
- TST-04 (8 test thực nghiệm Couchbase transactions) thuộc P0.
- Mỗi tiêu chí nghiệm thu (ACC-xx) của slice MUST có test tương ứng hoặc ghi rõ lý do thủ công.

### VII. Observability

- Structured logging, metrics, health check và OpenTelemetry tracing từ P0.
- Mọi request mang correlation id xuyên log, receipt và audit.
- Lỗi không tiết lộ dữ liệu của tài khoản khác; PII trong log theo policy.

## Ràng buộc kỹ thuật và phạm vi

- Runtime: .NET 10 LTS, ASP.NET Core Kestrel; không Native AOT/trimming.
- Persistence: Couchbase Server ≥ 7.6 khuyến nghị (tối thiểu 6.6.2); Couchbase .NET SDK
  ≥ 3.9.1 với transactions tích hợp; durability `Majority`; `txn_meta` tạo trước.
- Không Hangfire/Quartz; job phối hợp bằng lease document (JOB-01).
- Một deployment phục vụ đúng một game; `gameId` là hằng trong deployment config (TBD-11).
- Game config là file trong artifact deploy, phân phối qua `GetConfig` (CFG-03).
- Ngoài phạm vi: Leaderboard, League (owner 26/10/2026), cùng danh sách ở mục 2.5 của tài liệu
  yêu cầu.
- Nguồn sự thật yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` (rev hiện
  hành, local, gitignored). Spec, plan và task MUST trích ID yêu cầu (vd DAT-04, SEC-08,
  ACC-53); không tự thêm yêu cầu trái với tài liệu này.

## Quy trình phát triển

- Dùng Spec Kit: constitution → specify → (clarify) → plan → tasks → (analyze) → implement.
- P0 chia nhiều spec theo slice, làm tuần tự: 001 platform-core → 002 identity/session →
  003 SampleGame commands → 004 GM → 005 observability/ops → 006 Unity SDK.
- Commit theo conventional commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`); không thêm
  chữ ký/co-author Claude; identity `hung-bui-121`. `Requirements/` không commit.
- Một task chỉ "xong" khi build sạch (warnings-as-errors cho project framework) và test liên
  quan pass.

## Governance

- Constitution này đứng trên mọi quy ước khác của repo; tài liệu yêu cầu đứng trên constitution
  về nội dung nghiệp vụ.
- Sửa đổi: ghi Sync Impact Report, tăng version theo semver (MAJOR: bỏ/định nghĩa lại nguyên
  tắc; MINOR: thêm nguyên tắc/mục; PATCH: câu chữ), cập nhật ngày Last Amended.
- Mỗi plan MUST có mục Constitution Check; vi phạm phải ghi trong Complexity Tracking kèm lý do.
- Review (tech-lead-reviewer hoặc người) MUST kiểm nguyên tắc I–VII trước khi merge.

**Version**: 1.0.0 | **Ratified**: 2026-10-26 | **Last Amended**: 2026-10-26
