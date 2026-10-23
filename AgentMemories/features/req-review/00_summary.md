# RESUME — ĐỌC ĐẦU TIÊN (cập nhật 2026-10-23)

**Trạng thái:** Requirement **v1.2 rev 3** là bản hiện hành (`Requirements/Game_Server_System_Requirements_v1.2.md`, local, gitignored). Mọi TBD đã chốt hoặc có giả định thiết kế; không còn [VERIFY] chặn P0. Chưa có dòng code nào.

**Còn mở:**
1. Vòng 3 trên trang quyết định https://claude.ai/artifact/LW7xjjfNrJFPaBGVZwTEre — 1 mục `answers/R3-17-login` (TBD-17: Login chỉ trả `configVersion`, bundle qua `GetConfig`). Đọc bằng ArtifactData `list answers`; nếu owner đã trả lời → áp vào v1.2 (rev 4, sửa D7/CFG-03, bỏ nhãn [MẶC ĐỊNH] cuối).
2. TBD-09 (RPO/RTO), TBD-21 (GM frontend stack) Mở — chặn P0.5, không chặn P0.

**Bước kế tiếp đã đề xuất với owner (chưa xác nhận chạy):** system-architect thiết kế P0 từ v1.2 rev 3 (solution structure, `IModule`/`PlayerAggregateDescriptor`/`CommandDescriptor`, transaction runner + `GetMulti`, Platform.Web Protobuf, Identity PGS/Game Center, SampleGame 5 command, GM backend tối thiểu) → task-planner chia work item → code-implementer. Sprint 0 phải chạy TST-04 (8 test thực nghiệm Couchbase transactions).

**File trong thư mục này (đọc theo nhu cầu):** 01 chất lượng yêu cầu v1.0 · 02 độ phù hợp kỹ thuật v1.0 · 03 review chéo v1.1 · 04 câu trả lời owner (vòng 1 + 2) · 05 thiết kế lại theo owner (nhiều aggregate, transaction) · 06 xác minh Couchbase transactions. Lịch sử chi tiết các rev ở dưới.

**Quy ước repo:** commit conventional (`feat:/fix:/docs:/chore:`), KHÔNG chữ ký Claude; identity hung-bui-121; `Requirements/` không commit.

---

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

## v1.1 rev 2 (2026-10-21)

- `Requirements/Game_Server_System_Requirements_v1.1.md` đã viết (rev 1) theo 7 bước ở 01 §8 + toàn bộ 02 §13/§14, rồi được system-architect review chéo: `03_v11_architect_review.md` (1 Blocker, 6 Major, 21 Minor). **Rev 2 đã áp dụng cả 28 finding**, không bỏ qua finding nào; changelog "v1.1 → v1.1 rev 2" ở cuối tài liệu liệt kê từng finding.
- Trạng thái: Draft v1.1 rev 2 — chờ owner chốt. Thống kê: 167 yêu cầu có ID (159 MUST / 8 SHOULD; P0 = 119, P0.5 = 32, P0.5* = 3, P1 = 11, P1* = 1, P2 = 1), 52 ACC, 26 TBD, **36 chỗ `[MẶC ĐỊNH — chờ owner chốt]`** thuộc 15 TBD, 22 chỗ `[VERIFY]` (V1–V14, V14 mới: Hangfire/Quartz không có storage Couchbase).
- Thay đổi hướng so với 00 bảng đầu: D2 "receipt" của 00/02 §3 = "idempotency entry + receipt compact ≤ 512 B" của v1.1; receipt đầy đủ là projection `receipts` không dùng expiry, purge 180 ngày (TBD-08). D3: 4 section cố định P0 (`profile/progression/wallet/inventory`). DAT-04 có cơ chế (c) claim-marker saga. TBD-18 mặc định **Có** (IAP-01/02 + MIG-01 = P0.5). Battle state nằm trong aggregate (`activeBattles[]`), không battle document.

## v1.2 (2026-10-22)

- Owner trả lời 23/26 TBD (`04_owner_answers.md`): 8 thay đổi (TBD-07 **nhiều aggregate** — đảo ngược D1; TBD-11 một game/deployment; TBD-04a single session + takeover; TBD-01 5 sample command gồm IAP; TBD-02 một battle; TBD-03 PGS + Game Center; TBD-17 config = file; TBD-18 **Không** di dời), 15 chấp nhận, 3 mở (06, 09, 21). System-architect thiết kế lại ở `05_owner_decisions_design.md` (§I bảng văn bản mới, §J [MẶC ĐỊNH]/[VERIFY] mới, §K rủi ro).
- `Requirements/Game_Server_System_Requirements_v1.2.md` (file mới, v1.1 rev 2 giữ nguyên) áp dụng toàn bộ §I: mọi command = **một Couchbase transaction** (aggregate chạm ≤ 4 + receipt document + claim marker); receipt = idempotency entry + outbox carrier; không còn receipt ring/outbox nhúng/tầng A–B/saga; `pa_<aggregateId>` collection per aggregate; section ≡ aggregate; SMP-01..05; IAP-01/02 + ECO-08 + ACC-44/46/47 → P0; MIG/15.2/deliverable 9/G1/V5/V12/V13 xoá; V15–V22 thêm.
- Thống kê: 170 yêu cầu (162 MUST / 8 SHOULD; P0 = 127, P0.5 = 32, P1 = 10, P2 = 1), 53 ACC, 26 TBD (20 Chốt 22/10, 3 Mở, 9 còn [MẶC ĐỊNH] con), **31 chỗ `[MẶC ĐỊNH — chờ owner chốt]`** thuộc 9 TBD (07, 13, 02, 04a, 03, 01, 17, 15, 19), 27 chỗ `[VERIFY]` (V1–V4, V6–V11, V14–V22).

## v1.2 rev 2 (2026-10-22, owner duyệt vòng 2: 11/11)

- 9 mục chấp nhận → bỏ 21 nhãn `[MẶC ĐỊNH]`, TBD-07/13/02/04a/03/01/15/19 `Chốt 2026-10-22`; TBD-06 → **Giả định thiết kế** (5 000 DAU, 50 RPS command / 20 RPS login, p95 ≤ 300 ms, aggregate TB 8 KiB; chốt thật trước P0.5; bảng 12.1 + ACC-16).
- 2 thay đổi: R2-07-commit → **DAT-11** (P0, MUST): tập aggregate của command nạp bằng bulk/parallel get một round-trip, trong và ngoài transaction; **V23** mới (transactions .NET có cho `Get` song song không) chặn P0 cùng V15–V17. R2-17-config → CFG-03 viết lại: config phân phối qua Player API (Login trả `configVersion`, `GetConfig(configVersion?)` trả bundle Protobuf ≤ 1 MiB, client cache theo version); bỏ endpoint `/v1/config/...` và CDN; SEC-09/SMP-01/SDK-02/ACC-35 theo.
- Thống kê: 171 yêu cầu (163 MUST / 8 SHOULD; P0 = 128, P0.5 = 32, P1 = 10, P2 = 1), 53 ACC; **[MẶC ĐỊNH] còn 1 mục** (TBD-17: Login chỉ trả version, bundle qua `GetConfig` — ở D7 và CFG-03); 28 chỗ `[VERIFY]` (V1–V4, V6–V11, V14–V21, V23; V22 tham chiếu trong bảng).

## v1.2 rev 3 (2026-10-22, sau xác minh Couchbase transactions — `06_verify_couchbase_txn.md`)

- V4, V15, V16, V23 và phần cấu hình V17 → "Đã xác minh 22/10/2026" (mục 21 có cột kết luận + nguồn); **không còn [VERIFY] chặn P0**. V17 phần 1024 ATR/TTL/giới hạn document giữ [VERIFY] không chặn P0 → TST-04.
- Hệ quả thiết kế: **loại bỏ vĩnh viễn** fast path CAS thường cho `pa_*`/`rcpt::*` (V15: trộn ghi undefined kể cả CAS) — DAT-04/DAT-09 "không ngoại lệ", đọc ngoài transaction Read Committed hợp lệ; DAT-11 = `ctx.GetMulti` trong transaction (SDK ≥ 3.9.1, volatile → adapter), không `Task.WhenAll` trên `ctx.GetAsync`, chỉ tiêu "≈ 1 RTT" thay "1 round-trip"; GFT-03/04 counter ngoài lambda, hoàn trả theo kết quả transaction; DAT-07/OPS-05 `txn_meta` tạo trước, `MetadataCollection` giống hệt mọi instance, cleanup per Cluster, `CleanupWindow` 60 s, cần ≥ 1 instance chạy; Dev replicas = 0, `None` cấm; 2.2/TBD-20: transactions tích hợp `CouchbaseNetClient`, `Couchbase.Transactions` deprecated, pin SDK ≥ 3.9.1, Server ≥ 6.6.2 (khuyến nghị 7.6), `ExpirationTime` override 10 s; **TST-04** 8 test thực nghiệm (P0, CI).
- Thống kê: 172 yêu cầu (164 MUST / 8 SHOULD; P0 = 129, P0.5 = 32, P1 = 10, P2 = 1), 53 ACC; **còn 1 [MẶC ĐỊNH]** (TBD-17: Login chỉ trả `configVersion`, bundle qua `GetConfig` — D7, CFG-03) chờ vòng 3; [VERIFY] inline còn 18 chỗ (V1, V2, V3 phần còn lại, V6–V11, V14, V18–V21), không chặn P0.

## Việc tiếp theo

1. Owner vòng 3: duyệt mục `[MẶC ĐỊNH]` cuối cùng (TBD-17: Login chỉ trả `configVersion`, không nhúng bundle). TBD-09 (RPO/RTO) và TBD-21 (GM frontend stack) vẫn Mở, chặn P0.5 — không chặn P0.
2. System-architect thiết kế P0 từ DAT-09 + DAT-04(a/b/c) + DAT-11 (`GetMulti`) + API-10 + CMD-01..09 + CFG-03 + SMP-01..05; Sprint 0 chạy TST-04 (test thực nghiệm transactions) và đo V22.
3. Trước IAP/provider: xác minh V18–V21; trước khoá baseline: V1, V2 (TFM/serializer/OTel), V3 (RBAC/giới hạn collection), V6–V11, V14.

## Owner feedback vòng 1 (2026-10-22) — lịch sử, đã áp vào v1.2

- **TBD-11 / D4 — CHỐT:** mỗi deployment phục vụ MỘT game duy nhất. `gameId` là hằng trong deployment config; không multi-game routing/claim. Giữ scope `g_<gameId>` trong Couchbase để tái dùng cluster. → Cần sửa v1.1: ARC-09/ARC-10/DAT-01 bỏ nhánh "session claim", mục 2.x "hosting nhiều game chung deployment" = ngoài phạm vi hẳn.
- Các TBD còn lại owner trả lời trên trang: https://claude.ai/artifact/LW7xjjfNrJFPaBGVZwTEre (db collection `answers/<TBD-id>` = `{choice: accept|change|answer|later, note, updatedAt}`). (Đã áp. Vòng 2 ở `04_owner_answers.md`; vòng 3 xem đầu file.)
