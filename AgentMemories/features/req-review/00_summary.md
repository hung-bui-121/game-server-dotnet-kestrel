# RESUME — Review Game_Server_System_Requirements.md v1.0 (2026-10-21)

Tổng hợp từ hai báo cáo độc lập (đọc chi tiết khi cần):
- `01_requirements_quality.md` — chất lượng yêu cầu: 67 finding (4 Blocker / 28 Major / 35 Minor), ước lượng phase, 12 TBD bổ sung, thứ tự sửa tài liệu.
- `02_technical_fitness.md` — độ phù hợp kỹ thuật: điểm 1–5 cho 12 nhóm, 26 đề xuất sửa requirement, 18 nhóm yêu cầu mới, 13 fact cần xác minh.

## Kết luận chung

Tài liệu đúng bản chất (authoritative server, idempotency, CAS, security GM, không hứa quá) — **giữ làm nền**, không viết lại từ đầu. Vấn đề nằm ở **bốn quyết định nền chưa chốt** khiến ~40 yêu cầu hiện chỉ là "phải được công bố" không ai công bố:

| # | Quyết định nền | Cả hai báo cáo đều chỉ ra | Đề xuất chốt (mặc định) |
| --- | --- | --- | --- |
| 1 | **Player state = 1 aggregate document hay nhiều document** (TBD-07) | 01: F-E01 Blocker, chặn thiết kế P0. 02: chi phối nhóm 2/3/4/6/8 | 1 aggregate doc/player có `rev` + receipt ring 64 + outbox nhúng; command 1 aggregate = 1 CAS/sub-doc mutation; transaction đa doc chỉ cho thao tác được liệt kê (DAT-09 mới) |
| 2 | **Idempotency entry ≠ receipt ≠ ledger ≠ audit** | 01: F-A02 Blocker (GFT-05 vs CMD-07/DAT-03 mâu thuẫn thật). 02: §3.3 | Receipt = dedupe retry, window min(64 cmd, 24h). Invariant "một lần" = state riêng (claim marker insert-only, battle doc server cấp). Ledger/audit vĩnh viễn, sinh qua outbox cùng mutation |
| 3 | **Mô hình revision** | 01: F-A04. 02: §4 | Per-section `rev` đơn điệu (không dùng Couchbase CAS làm revision public); P0 trả full section; client áp dụng nếu `baseRev` khớp, ngược lại refetch section (API-10 mới) |
| 4 | **`gameId`/`env` đi vào hệ thống bằng đường nào** | 01: F-A06/F-B12. 02: DAT-01 | env = cluster/bucket, game = Couchbase scope, module = collection; gameId từ deployment config + claim, không từ payload |

## Blocker / rủi ro kỹ thuật hàng đầu

1. P0 ≈ 77% tổng effort, không có vertical slice; ACC-13/14 kéo Event/Leaderboard/League (P1/P2) vào điều kiện đóng P0; ACC-16/19 phụ thuộc TBD "trước production" → **P0 không đóng được khách quan**.
2. Receipt tách document không nguyên tử với state → double effect khi DB timeout (CMD-03/04).
3. Giftcode quota bằng CAS/transaction trên doc nóng → đề xuất atomic counter reserve-then-confirm + claim key insert-only.
4. SEC-08 "thiếu security profile ⇒ fail startup" không có cơ chế thực thi → cần deny-by-default + startup validator trên EndpointDataSource + architecture test CI.
5. Controllers + Application Parts (auto-discovery) xung đột ARC-04/ARC-07 → Minimal API + `IModule` map tường minh.
6. 4 project/module × ~8 module ≈ 30 project ở P0 → 2 project/module (`Module` + `Module.Adapters`), Contracts khi có consumer.
7. Protobuf + IL2CPP stripping/version lệch → pin Google.Protobuf, `link.xml` trong SDK, Unity IL2CPP smoke trong CI.
8. Thiếu OpenTelemetry → OBS-02 không kiểm chứng được.

## Gap (cả hai báo cáo đồng ý)

Remote/game config service · resource/item catalog · maintenance mode + version gate · ban/suspend · account deletion (store compliance, không phải "scope game") · server time · Unity client SDK (ai sở hữu?) · test strategy/CI (Testcontainers Couchbase, ≥2 instance) · IAP · flow reinstall → login provider đã link → đúng player cũ (IDN-10/11 mới) · có migrate Sniper PVP khỏi PlayFab không (TBD-18).

## Phân kỳ đề xuất

- **P0 (MVP vertical slice)**: Core/Host/module registration + startup validation; Platform.Web Protobuf+JSON + chuẩn lỗi; Couchbase + CAS + receipt ring + ledger; Identity **guest only** + session; Progression + Economy với **1** command mẫu + catalog; game config từ deploy; GM backend permission/scope/audit + frontend tối thiểu (lookup + adjust); log/metrics/health/OTel; template + module mẫu tối thiểu; Unity SDK tối thiểu + IL2CPP roundtrip; integration test Couchbase thật trong CI. ACC: 01(guest), 02, 04–07, 09, 10, 12, 13(P0), 17, 18.
- **P0.5 (production hardening)**: Google/FB link + conflict + recovery; Giftcode đầy đủ; GM SSO/CSRF/CORS/thu hồi quyền/Data Protection; GM frontend đầy đủ; maintenance/version gate/ban/deletion; load test + restore drill + runbook (ACC-16, 19).
- **P1/P2** giữ nguyên; ACC-14/15 chuyển sang P1.

## Lệch nhỏ giữa hai báo cáo (cần hợp nhất khi viết v1.1)

- Trùng ID: 01 dùng `API-10` = version gate, `OPS-08` = maintenance; 02 dùng `API-10` = revision model, `OPS-08` = secret mgmt, `GATE-01..02` = maintenance/version gate. → Khi viết v1.1 dùng prefix riêng (GATE, CFG, SDK, BAN, TIME, PRIV, TST, CI, MIG) theo 02; revision = API-10.
- 01 đề xuất Client SDK = `CLI-01..04`; 02 = `SDK-01..03`. → dùng SDK.
- IAP: 01 nói out-of-scope hoặc P1; 02 nói P0/P1 tuỳ lộ trình migrate Sniper PVP. → phụ thuộc TBD-18.

## Việc tiếp theo

1. Owner chốt 4 quyết định nền ở bảng trên + TBD sprint-0 (TBD-07, 11–15, 18, 20, 22).
2. Viết v1.1 theo thứ tự 7 bước ở `01_requirements_quality.md` §8, nhúng bảng sửa ở `02_technical_fitness.md` §13–14, kèm ma trận truy vết yêu cầu ↔ ACC ↔ phase.
3. Xác minh 13 fact ở `02_technical_fitness.md` §15 trước khi khoá baseline (.NET 10 EOL, Couchbase SDK/Server, Serilog rolling, Testcontainers, Google.Protobuf ↔ Unity…).
