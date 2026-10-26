# RESUME — P0 qua Spec Kit (cập nhật 2026-10-26)

**Trạng thái:** Spec Kit 1.2.4 đã cài (`specify init --here --integration claude --script ps`;
skill ở `.claude/skills/speckit-*`). Constitution v1.0.0 (`.specify/memory/constitution.md`).
Slice **001 platform-core**: spec + plan xong trên nhánh `001-platform-core` — **đang chờ owner review
spec + plan** (owner chọn dừng một lần sau spec+plan đầu tiên; sau khi duyệt thì tự chạy tiếp
tasks → implement và các slice sau).

**Quyết định owner 26/10/2026:**
- P0 chia 6 spec làm tuần tự: 001 platform-core → 002 identity/session (guest) → 003 SampleGame
  commands (economy, GetConfig, IAP verifier giả) → 004 GM backend + frontend tối thiểu →
  005 observability/ops → 006 Unity SDK.
- Không có Docker/Couchbase trên máy: unit test dùng store in-memory; integration/TST-04 viết sẵn,
  skip khi thiếu `COUCHBASE_CONNSTR`.
- Review gate: chỉ dừng sau spec + plan của 001.

**Môi trường:** SDK hệ thống chỉ có .NET 10 RC1 và không có ASP.NET Core 10 runtime → đã cài .NET SDK
10.0.403 vào `%USERPROFILE%\.dotnet10` (dotnet-install, không admin). Build/test bằng
`~/.dotnet10/dotnet.exe`. Package pin: research.md R2 của 001.

**Điểm thiết kế chính của 001 (plan.md / research.md):** runner DB-agnostic trong
`GameServer.Platform` trên `ITransactionalDocumentStore` (Couchbase + in-memory), ADR-0001;
`X-Request-Id` header; business rejection không tạo receipt (R5); claim marker GetOptional-then-Insert
(R6); error code string + số theo dải module (R8); `CommandResponse.result` = `resultCompact` (R9).

**Việc tiếp theo:** owner duyệt → `/speckit-tasks` → (`/speckit-analyze`) → `/speckit-implement`
cho 001; rồi specify/plan/tasks/implement 002..006.
