# AgentMemories — game-server-dotnet-kestrel

Đọc file này đầu phiên. Mỗi feature có thư mục riêng dưới `features/`; file `00_summary.md` trong mỗi thư mục là RESUME.

| Thư mục | Trạng thái | RESUME |
| --- | --- | --- |
| `features/req-review/` | DONE 2026-10-26 — v1.0 → v1.1 (rev 2) → owner trả lời TBD (04) + thiết kế lại (05) → `Requirements/Game_Server_System_Requirements_v1.2.md` **rev 4** (owner duyệt vòng 2 11/11, vòng 3 R3-17-login; xác minh Couchbase transactions 06; Leaderboard/League ngoài phạm vi framework); không còn [MẶC ĐỊNH], không còn [VERIFY] chặn P0 | `features/req-review/00_summary.md` |
| `features/p0-speckit/` | IN PROGRESS 2026-10-26 — Spec Kit; 001 platform-core DONE (+ review fixes), 002 identity-session DONE, 003 sample-game DONE; tiếp: 004 GM | `features/p0-speckit/00_summary.md` |

Code ở `src/`, `modules/`, `samples/`, `tests/`; specs Spec Kit ở `specs/`. Tài liệu yêu cầu (v1.0, v1.1 rev 2, **v1.2 rev 4 = hiện hành**) nằm trong `Requirements/` — thư mục này bị gitignore, không có trên remote.
