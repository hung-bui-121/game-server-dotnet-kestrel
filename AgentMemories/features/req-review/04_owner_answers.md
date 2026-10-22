# Owner answers — trang quyết định (2026-10-22)

Nguồn: https://claude.ai/artifact/LW7xjjfNrJFPaBGVZwTEre, db `answers/<TBD-id>`. 23/26 trả lời; TBD-06, TBD-09, TBD-21 chưa trả lời (= Để sau / Mở).

## THAY ĐỔI so với mặc định v1.1 rev 2

| TBD | Quyết định owner | Hệ quả chính |
| --- | --- | --- |
| TBD-07 | **Nhiều aggregate theo ranh giới nghiệp vụ** (Profile, Wallet, Inventory, Battle… — linh động theo define của từng game), mỗi aggregate có kích thước được kiểm soát; KHÔNG coi toàn bộ player là một aggregate | D1/DAT-09 viết lại; command chạm nhiều aggregate (BuyVirtualItem = Wallet + Inventory) cần transaction/saga trên hot path → D2 (receipt ring ở đâu), CMD-03/04, ECO-04, DAT-04 phải thiết kế lại |
| TBD-11 | Mỗi deployment phục vụ MỘT game duy nhất; `gameId` hằng trong deployment config; giữ scope `g_<gameId>` | ARC-09/10, DAT-01 bỏ nhánh session claim; multi-game hosting = ngoài phạm vi |
| TBD-04a | **Single session + takeover**: mỗi user một phiên duy nhất; login mới thu hồi phiên cũ | IDN-08 (max 10 → 1), IDN-10; cần mã lỗi `SESSION_REPLACED` + UX client |
| TBD-01 | Sample command mặc định mọi game đều có, làm trước: **Login** (trả player progression), **StartBattle**, **EndBattle**, **IAPurchase** (verify receipt Google/Apple), **BuyVirtualItem** | SampleGame P0 có 5 command; IAP vào P0 (sample) dù TBD-18 = Không |
| TBD-02 | Một thời điểm chỉ có **một battle**; kiểm chứng cả start và end battle | `activeBattles[]` N=3 → `activeBattle` đơn; Battle là aggregate riêng (theo TBD-07) |
| TBD-03 | Provider = **Google Play Games** và **Apple Game Center** | IDN-03/04: bỏ Google Sign-In + Facebook; thêm adapter PGS (server auth code) và Game Center (signature verify) |
| TBD-17 | **Game config là file riêng lẻ, không dính database** | CFG-01..03 viết lại: file deploy/mount, `configVersion` từ file, reload theo file; bỏ `PublishConfig` vào Couchbase; GM page config bỏ |
| TBD-18 | **Không** — framework mới hoàn toàn, chưa phục vụ game nào | MIG ngoài phạm vi; bỏ `legacy:{provider}` identity; bỏ mọi giá trị tạm lấy từ Sniper PVP (12.1, TBD-15 target, V13) |

## CHẤP NHẬN mặc định
TBD-04b, 04c, 05a, 05b, 07b, 08, 10, 12 (ghi chú: "đồng ý chỉ response các info thay đổi về để client cập nhật"), 13, 14, 15, 16, 19, 20, 22.

## CHƯA TRẢ LỜI (giữ Mở)
TBD-06 workload, TBD-09 RPO/RTO, TBD-21 GM frontend stack.
