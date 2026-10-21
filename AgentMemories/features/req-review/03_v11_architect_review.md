# Review chéo v1.1 — góc nhìn kiến trúc backend (2026-10-21)

Đối tượng: `Requirements/Game_Server_System_Requirements_v1.1.md` (1156 dòng). Chuẩn đối chiếu: `00_summary.md` (4 quyết định nền D1–D4), `02_technical_fitness.md` §3–§4, §13 (26 đề xuất sửa), §14 (18 yêu cầu mới), §15 (V1–V13); `01_requirements_quality.md` chỉ tra khi changelog viện dẫn (F-A02, F-B04..07, F-D01).

Quy ước mức: **Blocker** = system-architect không thể bắt đầu thiết kế P0 đúng hướng khi câu này còn nguyên; **Major** = owner chốt theo mặc định hiện tại sẽ gây rework hoặc tạo mâu thuẫn kỹ thuật thật; **Minor** = cần sửa câu chữ/phase/tham chiếu, không đổi thiết kế.

---

## 0. Kết luận

**v1.1 đủ làm cơ sở thiết kế SAU KHI sửa 1 Blocker + 6 Major.** Khung tài liệu (D1–D4, PCR, CMD/DAT là "nơi duy nhất", ACC theo phase, ma trận truy vết) đúng và nhất quán hơn v1.0 rất nhiều; ma trận Phụ lục C khớp 100% với thân (167 ID, mỗi ID đúng 1 lần ở thân + 1 lần ở ma trận; số đếm 159/8 và 115/35/3/12/1/1 chính xác). Vấn đề còn lại tập trung ở **một chỗ D1 bị vi phạm ngay trong command mẫu P0** (8.4), **ngân sách kích thước receipt ring/section chưa khớp với "full section"**, **DAT-04 thiếu cơ chế thứ ba (claim-marker saga) mà GFT/ECO-08/IAP đang dùng ngầm**, và **ba giá trị mặc định sai hướng** (TBD-18 = Không, receipt "không TTL", login 30/phút/IP).

Tổng: **1 Blocker, 6 Major, 21 Minor.**

---

## 1. Đánh giá 6 điểm task-planner tự quyết

| # | Quyết định | Đánh giá | Phải sửa gì |
| --- | --- | --- | --- |
| 1 | Idempotency entry (window) ≠ receipt (không TTL, projection `receipts` qua outbox); tra cứu CMD-09 | **Đồng ý về nguyên tắc** — đây là cách duy nhất thoả đồng thời F-A02 (01: receipt vĩnh viễn, tra được) và 02 §3.3 (ring 64 là dedupe). Nhất quán với DAT-09 **nếu** ring = idempotency entry + receipt *compact*, còn receipt *đầy đủ* chỉ sống ở projection. Chi phí thật: mỗi command = 1 `MutateIn` + relay inline (1 insert `receipts` + n insert `ledger` + 1 `MutateIn` xoá outbox) → **write amplification ×3–4 trên hot path**; chấp nhận được ở quy mô mobile HTTP nhưng phải ghi tường minh và đo (ACC-16). Ring bị FIFO đẩy ra thì receipt sống ở `receipts` — đúng, nhưng tài liệu hiện có hai lỗ: (a) 14.1 nói receipt chứa "delta/`SectionUpdate`" → 64 entry × full section phá ngân sách 128/256 KiB (M-01); (b) "không TTL" đối với command gameplay tần suất cao = tăng trưởng lưu trữ vô hạn (M-02) | M-01, M-02, m-01, m-10, m-13 |
| 2 | GATE/BAN/PRIV-01 = P0.5; TIME-01/OBS-04/OPS-08 = P0 | **Đồng ý** (theo 00 "Phân kỳ đề xuất", không theo 02 §14 vốn đặt tất cả P0). TIME-01 đúng phải P0 vì đổi shape mọi response; OBS-04 đúng P0 vì OBS-02 không kiểm được nếu thiếu. Chỉ cần tách "contract/slot" (P0) khỏi "feature" (P0.5) vì EXT-04, IDN-09, SDK-02 (đều P0) đã tham chiếu GATE/BAN | m-03 |
| 3 | SEC-15/16 = role/shared-key, HMAC = SEC-17; IDN-12 = không recovery từ local ID; PRIV-01 = deletion; SDK-01..04; BAN-01 + GM-07 | **Đồng ý toàn bộ**; changelog C.4 ghi rõ chỗ đổi số so với 01/02 nên không gây nhầm. Một nit kỹ thuật ở BAN-01 (tăng `sessionEpoch` cho cả ban scope command) | m-04 |
| 4 | IAP/MIG "P0.5*" theo TBD-18, mặc định = Không; ECO-08 grant nguồn ngoài qua `externalReference` | **Không đồng ý với mặc định "Không"** (M-05): tài liệu lấy Sniper PVP làm nguồn cho capacity (12.1, TBD-06), Unity target (TBD-15, V7), ngân sách document (V13) — tức là giả định ngầm "Có" — trong khi G2/TBD-18 mặc định "Không". Cơ chế "P0.5*" có điều kiện là đúng. ECO-08 đúng hướng nhưng claim marker + aggregate = 2 document, chưa có cơ chế DAT-04 nào cover (M-03) | M-03, M-05 |
| 5 | Số mặc định | Vô lý cho mobile: **login ≤ 30/phút/IP** (M-06 — CGNAT nhà mạng VN gom hàng nghìn player sau một IP; burst login sau push sẽ 429 người dùng thật). Cần align: **commit timeout 5 s** ngắn hơn `KvDurableTimeout` mặc định của SDK (m-06); **Pending tầng B 60 s** chỉ hợp lý cho transaction, không cho GM batch (m-07); **session sliding 30 ngày** hợp lý nhưng phải lazy-touch để không thêm 1 write/request (m-05); **drain 30 s** = đúng bằng `terminationGracePeriodSeconds` mặc định của k8s (m-20). Còn lại (90 ngày absolute, grace 30 ngày, cache 10 s/30 s/60 s, 200/100/50/50, 10/phút/localId, ring 64, 128/256 KiB) hợp lý | M-06, m-05, m-06, m-07, m-20 |
| 6 | Đổi số thứ tự mục | **Đồng ý.** Đã kiểm bằng script: mọi "mục N"/"Phụ lục X" trong thân và changelog trỏ đúng sau đánh số lại; "mục 12 cũ", "mục 15 cũ" ở C.5 ghi rõ là số cũ | — |

---

## 2. Findings

### 2.1. Blocker

#### B-01 — Command mẫu P0 (8.4) vi phạm D1: battle document tách riêng → `CompleteBattle` chạm 2 document

- **Vị trí**: 8.4 (dòng 372), DAT-03 (dòng 501, `battle` document có TTL), TBD-02 (dòng 694), Phụ lục B "Battle document TTL".
- **Vấn đề**: 8.4 nói `StartBattle` tạo `battle::{battleId}` (document riêng, TTL 7 ngày) rồi `CompleteBattle` "đánh dấu battle `rewarded` **trong cùng mutation** (hoặc trong claim marker nếu battle document tách riêng)". Hai vế loại trừ nhau: nếu battle là document riêng thì không có "cùng mutation" với aggregate; nếu dùng claim marker thì đây là saga 2–3 document trên **chính command mẫu dùng để chứng minh D1** ("command gameplay chạm một aggregate = một mutation"). Hệ quả: race hai `CompleteBattle` khác `requestId` cho cùng `battleId` trên hai instance — cả hai đọc battle chưa `rewarded`, cả hai CAS aggregate thành công lần lượt (aggregate không biết `battleId`) → trả thưởng hai lần, trừ khi thêm saga/marker. Tức là P0 sẽ được thiết kế trên đúng mẫu mà D1 muốn tránh.
- **Mức**: Blocker.
- **Sửa** (viết lại 8.4, đoạn 1):
  > Ví dụ: client gọi `StartBattle(requestId)` — đây là một command theo CMD-xx: server ghi một entry `activeBattles[]` **trong player aggregate** (`{battleId, startedAtUtc, configVersion, seed?}`, tối đa N = 3 entry `[MẶC ĐỊNH — chờ owner chốt, TBD-02]`, entry quá 7 ngày theo `IClock` coi như hết hạn và bị dọn ở mutation kế tiếp) và trả `battleId`. Client gửi `CompleteBattle(battleId, evidence, requestId)`; server kiểm tra entry tồn tại và chưa hết hạn, tính reward theo `configVersion` đã ghi trong entry, cập nhật progression/resource, **xoá entry**, ghi receipt và outbox — tất cả trong **một** CAS mutation (DAT-09). Không có battle document riêng ở P0. Battle document tách riêng chỉ khi evidence lớn (input log/replay, TBD-02) và khi đó áp dụng DAT-04(c).
  - DAT-03: bỏ `battle` document khỏi danh sách TTL, thay bằng "battle document (chỉ khi tách riêng theo 8.4)".
  - TBD-02 mặc định: "Evidence = eligibility-only; battle state trong aggregate, N = 3, hết hạn logic 7 ngày".
  - Phụ lục B: đổi dòng "Battle document TTL" → "Battle entry hết hạn logic / số entry tối đa: 7 ngày / 3".

### 2.2. Major

#### M-01 — Nội dung receipt ring chưa có ngân sách; 14.1 cho receipt chứa `SectionUpdate` → 64 entry phá giới hạn 128/256 KiB

- **Vị trí**: 14.1 bảng "Receipt" cột nội dung (dòng 521: "delta/`SectionUpdate`"), CMD-03 tầng A (dòng 362), DAT-09 (dòng 507), ACC-04 (dòng 610: "retry trả receipt đã commit với cùng `newRev`"), API-10 (P0 `full = true`).
- **Vấn đề**: nếu P0 trả `full = true` và receipt lưu `SectionUpdate` thì mỗi ring entry = một bản sao full section. Với section 10–30 KiB, 64 entry = 0,6–1,9 MiB → vượt giới hạn cứng 256 KiB ngay ở command thứ ~10. Ngược lại nếu ring chỉ lưu compact thì ACC-04 "trả receipt đã commit" phải được định nghĩa lại (trả gì khi retry?). Tài liệu chưa quyết.
- **Mức**: Major (sai hướng: owner chốt 14.1 như hiện tại → thiết kế ring không khả thi).
- **Sửa**:
  - DAT-09 thêm: "Mỗi ring entry ≤ 512 byte `[MẶC ĐỊNH — chờ owner chốt, TBD-13]`: `{requestId, operation, bodyHash, resultCode, receiptId, newRev[], configVersion, committedAtUtc, resultCompact}`; `resultCompact` là dữ liệu tối thiểu để dựng lại response (ví dụ danh sách reward id/số lượng), **không** chứa payload section. Ring 64 × 512 B = 32 KiB là phần cố định của ngân sách 128 KiB."
  - CMD-04/ACC-04 sửa: "Retry trong window trả **cùng `resultCode`, `receiptId`, `newRev` của receipt** kèm section hiện hành (`full = true`, `rev` hiện tại ≥ `newRev`); client áp dụng section hiện hành theo API-10." (Đây đúng là quy tắc resync 02 §4.1: `local.rev < newRev - 1 → refetch`, ở đây server gửi luôn.)
  - 14.1 cột nội dung Receipt: "delta/`SectionUpdate`" → "`resultCompact` (trong ring) / delta đầy đủ (trong projection `receipts`)".
  - CMD-07 thêm ghi chú thực thi: "Vế 24 h thực thi khi tra cứu: entry có `committedAtUtc` cũ hơn 24 h coi như không tồn tại; ring vẫn FIFO theo số lượng."

#### M-02 — "Receipt không TTL" cho mọi command gameplay = lưu trữ tăng vô hạn; chi phí projection chưa được ghi

- **Vị trí**: CMD-08 (dòng 367), 14.1 (dòng 521 "Không TTL"), TBD-08 mặc định (dòng 704 "receipt/ledger/audit không xoá"), DAT-03 (dòng 501), glossary Receipt (dòng 155).
- **Vấn đề**: receipt được sinh cho **mọi** command có actor Player (14.1 "Bắt buộc cho"). Ở 10–50 command/s trung bình, projection `receipts` tăng ~1–4 M document/ngày; "không xoá" là cam kết lưu trữ vô hạn cho dữ liệu mà giá trị tra cứu giảm nhanh sau vài tuần. Ledger/audit mới là bản ghi cần giữ dài hạn (và ít hơn nhiều vì chỉ sinh khi có delta resource/actor GM). Ngoài ra, chi phí write amplification của projection (xem mục 1 điểm 1) chưa xuất hiện ở bất kỳ yêu cầu nào → người đọc DAT-09 tưởng "một mutation" là toàn bộ chi phí.
- **Mức**: Major (mặc định sai hướng; owner chốt "không xoá" → cần archive job và ngân sách storage mà không ai lên kế hoạch).
- **Sửa**:
  - CMD-08/14.1/glossary: "không TTL" → "**không dùng Couchbase expiry** (không bị xoá tự động, không bị FIFO); retention bằng job archive/purge tường minh theo TBD-08 — mặc định: receipt projection 180 ngày, ledger và audit không xoá `[MẶC ĐỊNH — chờ owner chốt, TBD-08]`". GFT-05 không đổi (receipt giftcode vẫn sống ≥ 180 ngày, đủ cho khiếu nại).
  - DAT-09 hoặc JOB-02 thêm câu: "Chi phí chuẩn của một command tầng A: 1 `MutateIn` (authoritative) + relay inline best-effort: 1 insert `receipts`, 0..n insert `ledger`, 0..1 insert `gm_audit`, 1 `MutateIn` xoá outbox entry; relay thất bại → sweeper. Metric `outbox_relay_inline_failures_total` và `outbox_pending_age_seconds` bắt buộc (OBS-01)."
  - TBD-08 mặc định: "receipt 180 ngày; ledger/audit không xoá; session theo IDN-08; log 30 file".

#### M-03 — DAT-04 chỉ có (a) transaction / (b) outbox, nhưng GFT-03, ECO-08, IAP-02 (và 8.4 cũ) dùng cơ chế thứ ba: claim-marker saga

- **Vị trí**: DAT-04 (dòng 502), GFT-03 (dòng 400), GFT-04 (dòng 401), ECO-08 (dòng 352), IAP-02 (dòng 391), glossary Claim marker (dòng 163).
- **Vấn đề**: DAT-04 tuyên bố là "nơi duy nhất" cho atomicity ≥ 2 document và chỉ cho phép (a) hoặc (b). Saga giftcode (marker `pending` → counter → CAS aggregate → marker `completed` + reaper) có bước ghi **đầu tiên** là claim marker, không phải aggregate → không phải (b); không dùng transaction → không phải (a). ECO-08 nói "grant trùng `externalReference` bị từ chối bằng claim marker insert-only" nhưng không nói thứ tự marker ↔ CAS aggregate, cũng không nói reaper — nên IAP-02 và mọi grant nguồn ngoài sẽ được mỗi module tự chế một saga. Đây chính là chỗ "ngầm cần transaction đa document trên hot path" còn sót: ở P0.5 redeem giftcode và IAP là hot path thật.
- **Mức**: Major.
- **Sửa**:
  - DAT-04 thêm cơ chế **(c) claim-marker saga**: "Khi hiệu ứng 'một lần' có khoá tự nhiên ngoài aggregate (`gclaim::{code}::{playerId}`, `extref::{sourceType}::{externalReference}`, `membership::{season}::{playerId}`): (1) `Insert` marker `{state: pending, requestId, leaseUntil}` — thất bại vì đã tồn tại → nếu `completed` trả `ALREADY_CLAIMED`/`ALREADY_GRANTED`; nếu `pending` cùng `requestId` → tiếp tục bước 2; nếu `pending` khác `requestId` và chưa quá `leaseUntil` → 409 `COMMAND_IN_PROGRESS`; (2) [tuỳ chọn] reserve counter; (3) CAS mutation aggregate ghi state + receipt (ring) + outbox, receipt ghi `markerKey`; (4) `Replace(cas)` marker → `completed` (best-effort; sweeper suy ra từ receipt projection). Reaper: marker `pending` quá `leaseUntil` và không có receipt tương ứng trong aggregate/projection → hoàn trả counter và xoá marker. Retry cùng `requestId` luôn tra ring trước (CMD-04)."
  - GFT-03/GFT-04: thay mô tả saga bằng "theo DAT-04(c) với counter ở bước 2".
  - ECO-08: "…bị từ chối bằng claim marker insert-only **theo DAT-04(c)**, khoá `extref::{sourceType}::{externalReference}`".
  - IAP-02: "…(claim marker insert-only theo DAT-04(c), ECO-08)".
  - DAT-04 câu mặc định TBD-07: "(b) cho mọi command gameplay và GM một player; **(c) cho giftcode, grant nguồn ngoài, membership**; (a) chỉ cho GM cross-aggregate, event close, season rollover".

#### M-04 — API-10 "1–2 section" + `full = true` mâu thuẫn với aggregate tới 256 KiB

- **Vị trí**: API-10 (dòng 277), glossary Section (dòng 159: "P0 thường chỉ có 1–2 section"), DAT-09 (dòng 507), 02 §4.1 ("section đủ nhỏ").
- **Vấn đề**: nếu aggregate (profile + progression + wallet + inventory) là **một** section thì mọi response command ở P0 mang full section tới 128–256 KiB Protobuf qua mạng di động — gấp 2–4 lần giới hạn request body 64 KiB và vô hiệu hoá lý do chọn `full = true` ("section đủ nhỏ"). Chốt "1–2 section" rồi tách sau sẽ đổi contract client (section id) sau khi SDK đã phát hành.
- **Mức**: Major (mặc định sai hướng).
- **Sửa**:
  - API-10: "P0 có **4 section cố định** trong player aggregate: `profile`, `progression`, `wallet`, `inventory`, mỗi section có `rev` riêng; mục tiêu kích thước mỗi section ≤ 32 KiB (metric cảnh báo) `[MẶC ĐỊNH — chờ owner chốt, TBD-12]`. Một mutation có thể tăng `rev` của nhiều section; response chỉ chứa section có thay đổi. Game thêm section qua `ModuleDescriptor` (ví dụ `event` ở P1)."
  - Glossary Section: bỏ "thường chỉ có 1–2 section".
  - DAT-09: "`rev` per-section (API-10, 4 section P0)".

#### M-05 — TBD-18 mặc định "Không" mâu thuẫn với chính tài liệu (và với mục đích framework)

- **Vị trí**: G2 (dòng 87), TBD-18 (dòng 714), 2.5 (dòng 107–108), 8.6, 15.2, 12.1 (dòng 476 "lấy từ telemetry Sniper PVP live"), TBD-06, TBD-15 ("target theo Sniper PVP hiện tại"), V7, V13.
- **Vấn đề**: tài liệu dùng Sniper PVP làm nguồn số liệu capacity, target Unity/IL2CPP, ngân sách document và phiên bản Google.Protobuf — tức là thiết kế **cho** Sniper PVP — nhưng mặc định TBD-18 lại nói framework không phục vụ di dời Sniper PVP, đẩy IAP sang P1 và MIG ra ngoài scope. 02 §11 nêu rõ mục tiêu "thay thế PlayFab cho Sniper PVP"; 00 chỉ ghi "phụ thuộc TBD-18", không khuyến nghị "Không". Nếu owner chốt theo mặc định, "P0.5 = game đầu tiên được phép lên Production" là production cho một game chưa tồn tại, trong khi game thật cần IAP ngay. Nếu owner chốt "Có" sau khi P0 đã thiết kế theo "Không": `playerId` không có chỗ cho PlayFabId mapping, aggregate không được đo theo V13, ECO-08/IAP không có trong vertical slice.
- **Mức**: Major.
- **Sửa**:
  - TBD-18 mặc định → "**Có** (IAP-01/02 = P0.5, MIG-01 = P0.5, MIG-02 = P1)"; G2 viết lại: "Framework phục vụ game mới **và** di dời Sniper PVP; nếu TBD-18 = Không thì IAP → P1, MIG → out-of-scope và các giá trị tạm lấy từ Sniper PVP (12.1, TBD-15, V13) phải được thay bằng nguồn khác".
  - Nếu owner vẫn muốn "Không": phải sửa đồng thời 12.1/TBD-06/TBD-15/V13 để không tham chiếu Sniper PVP — tài liệu không được vừa "không di dời" vừa "đo theo Sniper PVP".
  - IDN-01 thêm (độc lập TBD-18, chi phí 0): "`playerId` do server cấp là chuỗi opaque; mapping key dạng `legacy:{provider}:{id}` được dự trữ cho import (MIG-02)".

#### M-06 — Rate limit login ≤ 30/phút/IP không dùng được cho mobile sau CGNAT

- **Vị trí**: SEC-13 (dòng 436), Phụ lục B (dòng 803).
- **Vấn đề**: nhà mạng di động (VN và hầu hết thị trường) dùng carrier-grade NAT; hàng nghìn thiết bị chia sẻ một IP egress. Login burst sau push (chính kịch bản 12.1) sẽ vượt 30/phút/IP trong vài giây → 429 cho người dùng hợp lệ; và vì `ForwardedHeaders` lấy IP từ LB, mọi instance cùng chặn. Giới hạn per-IP thấp chỉ đúng cho web GM.
- **Mức**: Major (sai hướng; owner chốt theo sẽ gây sự cố ở load test hoặc production).
- **Sửa** SEC-13: "Login: ≤ M = 10 lần/phút/`localId` hoặc provider subject (kiểm soát chính); per-IP chỉ giới hạn **lần đăng nhập thất bại**: ≤ 60 thất bại/phút/IP (chống credential stuffing) và ngưỡng tổng ≤ 600 request login/phút/IP chỉ để **cảnh báo** (metric), không chặn `[MẶC ĐỊNH — chờ owner chốt, TBD-19]`. GM API giữ per-IP chặn cứng ≤ 30/phút." Phụ lục B cập nhật tương ứng.

### 2.3. Minor

| # | Vị trí | Vấn đề | Sửa |
| --- | --- | --- | --- |
| m-01 | DAT-03 (dòng 501), DAT-01 (dòng 499), CMD-03 tầng B | `receipts` vừa chứa receipt projection (không expiry) vừa chứa entry tầng B `Pending` (TTL) → không đặt được collection `maxTTL` làm "lưới an toàn" như DAT-03 yêu cầu | Tầng B dùng collection riêng `command_pending` (P1) có `maxTTL`; `receipts` không `maxTTL`. DAT-03 sửa "idempotency entry (tầng B trong `command_pending`)" |
| m-02 | OPS-05 (dòng 555) vs JOB-02 (dòng 451), ACC-27 (dòng 633) | OPS-05 ghi sweeper outbox = P0.5; JOB-02 và ACC-27 (P0) đòi sweeper ở P0 ("audit tồn tại ngay cả khi relay bị tắt") | OPS-05: "P0 (sweeper outbox); P1 (job)" |
| m-03 | GATE-01/02, BAN-01 (P0.5) vs EXT-04, IDN-09, SDK-02 (P0) | Yêu cầu P0 tham chiếu hành vi P0.5; pipeline slot và mã lỗi phải có từ P0 để SDK/pipeline không đổi shape | Tách phase trong GATE-01/02 và BAN-01: "Middleware slot, header `clientVersion/platform/buildId`, trường `ban` trong aggregate, mã lỗi `MAINTENANCE/UPDATE_REQUIRED/ACCOUNT_SUSPENDED` trong catalog = P0; flag/GM page/audit/`player.ban` = P0.5" |
| m-04 | BAN-01 (dòng 318) | "Ban mới làm tăng `sessionEpoch`" áp dụng cả scope command → thu hồi mọi session dù login vẫn hợp lệ | "Ban scope `login` làm tăng `sessionEpoch`; ban scope command chỉ kiểm tại command (không thu hồi session)" |
| m-05 | IDN-08 (dòng 307) | Sliding TTL 30 ngày ngầm đòi `Touch` session mỗi request = +1 write/request trên hot path; multi-session không có trần → reinstall lặp tạo session vô hạn | Thêm: "Gia hạn sliding **lazy**: chỉ `Touch` khi lần gia hạn trước cách > 24 h; tối đa 10 session/player, tạo mới vượt trần thu hồi session cũ nhất `[MẶC ĐỊNH, TBD-14]`" |
| m-06 | PERF-02, Phụ lục B "Timeout bước commit 5 s" | Nhỏ hơn `KvDurableTimeout` mặc định của SDK (10 s) → timeout thực tế do SDK quyết, không phải 5 s; `Majority` xuyên AZ khi failover có thể > 5 s | "Timeout bước commit = `KvDurableTimeout` cấu hình = 5 s (set tường minh trong SDK options); vượt → CMD-04" |
| m-07 | CMD-03 tầng B (dòng 362) "Pending quá hạn 60 s" | GM batch có thể chạy > 60 s → reaper dọn `Pending` còn sống → chạy hai lần | "Tầng B chỉ cho command chạy trong **một** Couchbase transaction (timeout transaction ≤ 15 s → `Pending` quá hạn 60 s an toàn); GM batch/job dài dùng JOB-01 lease, không dùng tầng B" |
| m-08 | CFG-01 (dòng 380), GATE-02 (dòng 286) | P0 "reload không restart" nhưng nguồn là file deploy nạp khi startup và GM page ở P1 → không có đường nào tạo `configVersion` mới mà không restart; GATE-02 (P0.5) đòi GM sửa `minClientVersion` qua CFG nhưng CFG GM page là P1 | CFG-01: "P0 có lệnh ops/`Dev` endpoint `PublishConfig(file)` ghi version mới vào Couchbase"; GATE-02: "`minClientVersion` là document `gate` riêng do GM sửa (P0.5), không chờ CFG page" |
| m-09 | CMD-05 (dòng 364), API-05 | "`CasMismatch` → … rồi 409" không có `code` trong catalog | Thêm `CONCURRENT_UPDATE` (409, `retryable = true`, `retryAfter` nhỏ) vào API-05/API-11; SDK retry cùng `requestId` → ring lookup |
| m-10 | CMD-09 (dòng 368), 14.1, PRIV-01 | Tra cứu theo `requestId` **hoặc** `receiptId` cần hai key; PRIV-01 ẩn danh `playerId` sẽ phải re-key nếu key projection chứa `playerId` | "`receiptId` = hash xác định của (`playerId`, operation, `requestId`); key projection `rcpt::{receiptId}`, `playerId` là trường có index (ẩn danh = ghi đè trường). Ledger key `ledger::{entryId}` tương tự" |
| m-11 | DAT-09 (dòng 507) "`MutateIn` ≤ 16 path" | Một command thực tế cần ~10–13 path (3–5 state, 2 `rev`, push + trim ring, push outbox, `outboxPending`, `updatedAtUtc`) → sát trần; MutateIn trên document 100–256 KiB không rẻ hơn Replace đáng kể | "Commit mặc định = `Replace(cas)` toàn document; `MutateIn` là tối ưu khi đo thấy cần" (giữ V5 cho trường hợp dùng MutateIn) |
| m-12 | DAT-09 câu cuối, SEC-14, ACC-27 | "Mọi document nghiệp vụ khác dùng cùng hình dạng `rev` + outbox" chưa liệt kê; ACC-27 đòi audit cho bật maintenance, đổi quyền staff → `gate`, `gm_staff`, `giftcodes`, `config` đều phải có outbox | Liệt kê tường minh: "`giftcodes`, `gate` (maintenance/minClientVersion), `gm_staff`, `config` là aggregate có outbox để SEC-14 đi cùng mutation" |
| m-13 | 2.3 D2 (dòng 76) vs 00 D2 | 00 định nghĩa "Receipt = dedupe retry, window min(64, 24 h)"; v1.1 đổi "receipt" thành bản ghi bền vững và đặt "idempotency entry" vào vai trò đó — đúng, nhưng là **thay đổi thuật ngữ so với chuẩn đối chiếu** | Thêm một dòng ở changelog C.1: "Thuật ngữ 'receipt' trong 00/02 §3 tương ứng 'idempotency entry' của v1.1" |
| m-14 | ARC-09 (dòng 235) | "của mọi document key/scope" đọc được là `gameId` nằm trong cả key lẫn scope; DAT-01/D4 nói game = scope, document chỉ mang trường | "…của scope (Couchbase scope `g_<gameId>`) và là trường trong document; không lặp `gameId` trong document key" |
| m-15 | DAT-10 (dòng 508) | "`rev` (nếu là aggregate)" là một trường, nhưng player aggregate có `rev` per-section | "`rev` per-section (player aggregate) hoặc `rev` đơn (aggregate khác)" |
| m-16 | PCR-01 (dòng 181) | "Thay đổi giá trị PCR là breaking change theo ARC-08" → đổi một ngưỡng rate limit sau load test = major version | Tách: "Mục PCR semantic (section, window, error code, consistency) = breaking; mục vận hành (Phụ lục B) = non-breaking, ghi changelog" |
| m-17 | [VERIFY] | IDN-08 expiry 30/90 ngày phụ thuộc V6 nhưng không gắn nhãn; 2.2 "Couchbase SDK dùng reflection và serializer động" khẳng định như fact (thuộc V2); 2.2 "Hangfire/Quartz không có storage Couchbase chính thức" là fact chưa xác minh | Gắn "[VERIFY] V6" vào IDN-08, "[VERIFY] V2" vào câu reflection, thêm V14 cho Hangfire/Quartz. Ngoài ba chỗ này, thân tài liệu **không** khẳng định điều đang [VERIFY] như fact (đã rà V1–V13) |
| m-18 | SEC-13 (P0) → PERF-05 (P0.5) | Concurrency limiter riêng cho adjust/redeem viện dẫn yêu cầu P0.5 | SEC-13: "(PERF-04 ở P0; budget nhóm theo PERF-05 ở P0.5)" |
| m-19 | IDN-01/IDN-02/IDN-05 | First-login guest = 3 insert (`players`, `identities`, `sessions`); link = insert `identities` + CAS aggregate. Thứ tự và xử lý orphan chưa ghi → mỗi người làm một kiểu | Thêm vào IDN-01: "Tạo player: insert aggregate → insert mapping (thua race → xoá aggregate vừa tạo, trả player thắng) → insert session. Link: insert mapping trước, CAS aggregate sau; aggregate chỉ giữ danh sách hiển thị" |
| m-20 | OPS-03, Phụ lục B "Drain ≤ 30 s" | Bằng đúng `terminationGracePeriodSeconds` mặc định k8s → SIGKILL trước khi flush log | "Drain ≤ 25 s; deployment đặt grace period ≥ drain + 10 s (ghi OPS runbook)" |
| m-21 | LDB-01 backend A (P1) | Group document ≤ 200 member nhận score từ outbox của 200 player → CAS contention trên một document; chấp nhận được vì ngoài request path nhưng chưa ghi | Thêm: "Backend A: ghi score qua relay/sweeper (ngoài request), CAS retry không giới hạn 5 lần của CMD-05; đo contention trước khi chọn cho board > 50 member hoạt động đồng thời" |

---

## 3. Kiểm tra nhất quán với DAT-09 — nơi còn ngầm cần ghi nhiều document trên hot path

| Yêu cầu | Tôn trọng DAT-09? | Ghi chú |
| --- | --- | --- |
| CMD-03/CMD-04 tầng A | Có | Ring + state + outbox cùng mutation; CMD-04 đọc lại aggregate — đúng mẫu 02 §3.1 |
| ECO-03/04/06 | Có | Ledger qua outbox; không transaction |
| SEC-14 audit | Có (với m-12) | Chỉ đúng khi mọi document GM sửa có outbox — phải liệt kê |
| JOB-02 outbox P0 | Có (với m-02) | Sweeper phải là P0 |
| API-10 per-section rev | Có (với M-04) | 4 section trong một document, một mutation tăng nhiều `rev` |
| 8.4 CompleteBattle | **Không** → B-01 | 2 document trên command mẫu P0 |
| GFT-03/04 | Ngoài (a)/(b) → M-03 | Saga 3 document, cần DAT-04(c) |
| ECO-08 / IAP-02 | Ngoài (a)/(b) → M-03 | Claim marker + aggregate |
| IDN-01 first login / IDN-05 link | Chấp nhận được (m-19) | Insert-only + thứ tự; không cần transaction nhưng phải ghi thứ tự |
| PRIV-01 | Có | Workflow/job, không hot path |
| LDB-01 A (P1) | Có (m-21) | Ngoài request path |

Kết luận mục này: sau B-01 và M-03, **không còn** command hot path nào cần Couchbase transaction; danh sách (a) trong DAT-04 (GM cross-aggregate, event close, season rollover) là đúng và đủ.

---

## 4. Các giá trị `[MẶC ĐỊNH — chờ owner chốt]` — sai hướng

Thân tài liệu có 30 chỗ gắn nhãn (32 lần xuất hiện trừ 2 chỗ mẫu ở dòng 15/131), thuộc 15 TBD. Đánh giá:

| TBD | Mặc định | Hướng | Finding |
| --- | --- | --- | --- |
| TBD-18 | Không di dời Sniper PVP | **Sai hướng** | M-05 |
| TBD-13 (phần receipt) | Receipt không TTL | **Sai hướng** | M-02 |
| TBD-12 | 1–2 section, full | **Sai hướng** | M-04 |
| TBD-19 (login/IP) | 30/phút/IP | **Sai hướng** | M-06 |
| TBD-02 | Battle doc riêng TTL 7 ngày | **Sai hướng** | B-01 |
| TBD-07 (DAT-04) | (b) cho mọi gameplay | Đúng nhưng thiếu (c) | M-03 |
| TBD-14 | 30/90 ngày, thu hồi ≤ 60 s | Đúng (cần lazy touch) | m-05 |
| TBD-19 (commit 5 s, drain 30 s) | | Đúng, cần align | m-06, m-20 |
| TBD-13 (Pending 60 s) | | Đúng cho transaction, sai cho batch | m-07 |
| TBD-07b, 11, 16, 17, 20, 04a, 04c, 05a, 05b, 10, 15 | | Đúng hướng | — |

---

## 5. §13/§14 của báo cáo 02 áp dụng sai nghĩa?

Rà 26 dòng §13 và 18 dòng §14 với thân v1.1: **không có dòng nào bị áp dụng sai nghĩa**. Ba chỗ là *mở rộng có chủ ý* cần ghi nhận (không phải lỗi):

1. §13 CMD-03 / §3.1: 02 gọi ring là "receipt"; v1.1 đổi thành "idempotency entry + receipt compact" (m-13 — ghi vào changelog).
2. §14 GATE/BAN/PRIV/TST-03 phase P0 → v1.1 P0.5 theo 00; hợp lệ (m-03 tách contract/feature).
3. §14 DAT-09: v1.1 thêm "mọi document nghiệp vụ khác dùng cùng hình dạng `rev` + outbox" — tốt, nhưng cần liệt kê (m-12).

Một chỗ thân tài liệu **mạnh hơn** 02 mà không ghi nguồn: CFG-01 "GM sửa qua page ở P1" trong khi 02 §14 CFG để GM/env override ở P0 — chấp nhận được vì P0 là vertical slice, nhưng sinh m-08.

---

## 6. Ma trận truy vết (Phụ lục C) và [VERIFY]

- Kiểm bằng script: 167 dòng, 159 MUST / 8 SHOULD, phase 115/35/3/12/1/1 — khớp dòng 823. Mỗi ID xuất hiện đúng 1 lần trong bảng yêu cầu và 1 lần trong ma trận.
- Kiểm tay 24 ID (ARC-10, API-06, API-09, IDN-09, IDN-12, ECO-05, CMD-03, CMD-08, GFT-03, SEC-01, SEC-08, SEC-14, PERF-10, DAT-07, LOG-04, OPS-05, JOB-02, SDK-02, GM-04, PLY-04, TST-01, CFG-03, PRIV-01, EXT-01): cột ACC = hợp của "Kiểm bằng" và "Kiểm cho yêu cầu" đúng ở tất cả; cột TBD đúng kể cả chỗ yêu cầu chỉ nhắc TBD trong câu mà không gắn nhãn (IDN-12 → TBD-04c, CMD-08 → TBD-08).
- Danh sách ACC theo phase ở 2.6 khớp cột Phase của bảng 18 (đã đối chiếu 52 ACC).
- [VERIFY] V1–V13: mọi chỗ dùng đều gắn nhãn, trừ ba chỗ ở m-17 (IDN-08 ↔ V6; 2.2 reflection ↔ V2; Hangfire/Quartz chưa có V). Không có chỗ nào khẳng định ngược với một V đang mở.

---

## 7. v1.1 đủ làm cơ sở thiết kế chưa?

**Có — sau khi sửa Blocker + Major.** Thứ tự sửa đề xuất (mỗi bước độc lập, có thể làm trong một lần sửa v1.2):

1. **B-01** — viết lại 8.4 (battle state trong aggregate), sửa DAT-03/TBD-02/Phụ lục B.
2. **M-03** — thêm DAT-04(c) claim-marker saga; GFT-03/04, ECO-08, IAP-02 tham chiếu; cập nhật mặc định TBD-07.
3. **M-01** — ngân sách ring entry ≤ 512 B, định nghĩa response retry; sửa 14.1, CMD-04, ACC-04, CMD-07.
4. **M-04** — 4 section cố định P0 trong API-10/glossary/DAT-09.
5. **M-02** — "không TTL" → "không expiry + retention job"; ghi chi phí projection; TBD-08 mặc định.
6. **M-05** — TBD-18 mặc định "Có" (hoặc gỡ mọi phụ thuộc Sniper PVP); IDN-01 dự trữ `legacy:` mapping.
7. **M-06** — SEC-13/Phụ lục B rate limit login.
8. Minor theo cụm: phase (m-02, m-03, m-18) → số/timeout (m-05, m-06, m-07, m-20) → key/collection (m-01, m-10, m-11, m-12, m-14, m-15) → câu chữ/VERIFY/changelog (m-04, m-08, m-09, m-13, m-16, m-17, m-19, m-21).

Sau v1.2, các TBD Sprint 0 (dòng 720) có thể chốt mà không cần đọc lại báo cáo này; system-architect bắt đầu thiết kế P0 từ DAT-09 + DAT-04(a/b/c) + API-10 (4 section) + CMD-01..09.
