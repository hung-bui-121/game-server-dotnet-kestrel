# AgentMemories — game-server-dotnet-kestrel

Đọc file này đầu phiên. Mỗi feature có thư mục riêng dưới `features/`; file `00_summary.md` trong mỗi thư mục là RESUME.

| Thư mục | Trạng thái | RESUME |
| --- | --- | --- |
| `features/req-review/` | DONE 2026-10-22 — v1.0 → v1.1 (rev 2) → owner trả lời TBD (04) + thiết kế lại (05) → `Requirements/Game_Server_System_Requirements_v1.2.md` **rev 3** (owner duyệt vòng 2 11/11; xác minh Couchbase transactions 06: V4/V15/V16/V17/V23 đã xác minh, fast path CAS loại bỏ hẳn, DAT-11 = `GetMulti`, TST-04 test thực nghiệm); không còn [VERIFY] chặn P0; còn 1 [MẶC ĐỊNH] (TBD-17 Login chỉ trả version) chờ vòng 3 | `features/req-review/00_summary.md` |

Chưa có code. Tài liệu yêu cầu (v1.0, v1.1) nằm trong `Requirements/` — thư mục này bị gitignore, không có trên remote.
