# Đánh giá chất lượng yêu cầu — Game Server System Requirements v1.0

| Thuộc tính | Giá trị |
| --- | --- |
| Tài liệu được đánh giá | `Game_Server_System_Requirements.md` v1.0 (grooming draft, 21/10/2026) |
| Ngày đánh giá | 21/10/2026 |
| Trục đánh giá | (1) Mâu thuẫn/chồng chéo, (2) Thiếu sót, (3) Tính kiểm chứng, (4) Phân kỳ P0/P1/P2, (5) TBD, (6) Cấu trúc tài liệu |
| Ngoài phạm vi đánh giá | Kiến trúc kỹ thuật, lựa chọn công nghệ, thiết kế code — do agent khác đảm nhiệm |
| Bối cảnh | Team đang vận hành Sniper PVP trên PlayFab + Azure Functions .NET 8 với GM tool riêng; framework mới chưa có dòng code nào |

## 0. Tóm tắt điều hành

Tài liệu có chất lượng khá cao cho một grooming draft: ranh giới scope rõ (mục 2.3), ngôn ngữ cẩn trọng (không hứa anti-cheat tuyệt đối, không suy capacity từ tên framework), và đã nhìn thấy hầu hết bẫy consistency của một game server authoritative. Tuy nhiên, với tư cách cơ sở để **thiết kế và lập kế hoạch**, tài liệu còn bốn vấn đề hệ thống:

1. **Cùng một khái niệm được quy định ở nhiều chỗ với sắc thái khác nhau** (idempotency 8 chỗ, atomicity đa-document 5 chỗ, revision 5 chỗ, audit 4 chỗ) và chưa có định nghĩa phân biệt *idempotency entry / receipt / ledger / audit* — dẫn tới ít nhất một mâu thuẫn thật (GFT-05 vs CMD-07/DAT-03).
2. **P0 quá nặng**: ước lượng tương đối cho thấy P0 chiếm ~75–80% tổng effort của cả ba phase, đồng thời ACC-13/ACC-14 ngầm kéo Event/Leaderboard/League (P1/P2) vào điều kiện hoàn thành P0.
3. **Khoảng 70% yêu cầu không có kịch bản nghiệm thu (ACC) tương ứng**, và ~25 yêu cầu dùng "được công bố"/"phù hợp"/"rõ ràng" mà không nói ai công bố, ở đâu, khi nào.
4. **Một số năng lực vận hành bắt buộc của game live bị thiếu hẳn**: remote/game config, item catalog, IAP, maintenance mode/version gate, ban/suspend, account deletion theo chính sách store, cheat tooling cho QA, vị trí thực của `gameId`.

### Thống kê finding

| Trục | Blocker | Major | Minor | Tổng |
| --- | --- | --- | --- | --- |
| 1. Mâu thuẫn / chồng chéo | 2 | 6 | 8 | 16 |
| 2. Thiếu sót (gap) | 0 | 11 | 13 | 24 |
| 3. Tính kiểm chứng | 0 | 4 | 5 | 9 |
| 4. Phân kỳ P0/P1/P2 | 1 | 2 | 0 | 3 |
| 5. TBD | 1 | 4 | 2 | 7 |
| 6. Cấu trúc tài liệu | 0 | 1 | 7 | 8 |
| **Tổng** | **4** | **28** | **35** | **67** |

Thang mức độ:
- **Blocker**: không thể bắt đầu thiết kế P0 hoặc không thể đóng P0 nếu không sửa.
- **Major**: gây hiểu sai giữa các nhóm, nghiệm thu không khách quan, hoặc tạo rework đáng kể ở phase sau.
- **Minor**: làm giảm độ rõ/độ gọn; sửa được trong lần soát tài liệu kế tiếp.

---

## 1. Mâu thuẫn / chồng chéo nội bộ

### F-A01 — Idempotency quy định ở 8 chỗ; "request ID" và "idempotency key" dùng lẫn nhau
- **Liên quan**: API-07, CMD-01, CMD-02, CMD-04, CMD-07, GFT-04, EVT-01, GM-03, PERF-06, ACC-04, ACC-05
- **Vấn đề**: CMD-01 định nghĩa *idempotency key* = (game/environment, actor, operation, request ID) — request ID chỉ là một thành phần. Nhưng API-07, PERF-06 và ACC-05 lại nói "cùng request ID" như thể request ID là key. CMD-02 nói "cùng key với nội dung khác phải bị từ chối", ACC-05 nói "cùng request ID với nội dung khác" — nếu hai actor khác nhau tình cờ dùng cùng request ID thì theo CMD-01 là hai key khác nhau, theo ACC-05 là xung đột. GFT-04 và EVT-01 lặp lại bảo đảm idempotency bằng ngôn ngữ riêng thay vì tham chiếu CMD-*.
- **Mức độ**: Major
- **Đề xuất**:
  - Thêm vào glossary (mục 3): *Request ID* — định danh do client sinh cho một lần gửi command; *Idempotency key* — tổ hợp theo CMD-01, do server dựng, không bao giờ do client gửi nguyên vẹn.
  - Sửa API-07 thành: "Command thay đổi trạng thái phải nhận `requestId` do client sinh; server dựng idempotency key theo CMD-01 và thực thi theo CMD-02..CMD-04."
  - Sửa ACC-05: "Cùng actor, cùng operation, cùng `requestId` nhưng payload khác → bị từ chối với mã lỗi X; không có cập nhật thêm."
  - GFT-04, EVT-01, GM-03: thay câu idempotency riêng bằng "tuân thủ CMD-01..CMD-07".

### F-A02 — Receipt vừa là cache ngắn hạn vừa là bản ghi vĩnh viễn: GFT-05/ECO-06 mâu thuẫn CMD-07/DAT-03
- **Liên quan**: GFT-05, ECO-06, CMD-03, CMD-04, CMD-07, DAT-03, glossary "Receipt"
- **Vấn đề**: GFT-05 yêu cầu "retry lượt đã thành công vẫn tra được kết quả" và "sửa/vô hiệu code không thay đổi receipt cũ" (ngụ ý receipt tồn tại lâu dài). CMD-07 cho phép "cache idempotency ngắn hạn hết hạn"; DAT-03 cho phép TTL với "receipt ngắn hạn phù hợp CMD-07". Vậy sau replay window, client retry claim giftcode nhận gì? Theo CMD-07 "business invariant nhận quà một lần phải tồn tại" → server từ chối, nhưng GFT-05 nói phải "tra được kết quả". Hai câu không thể cùng đúng nếu receipt bị TTL. Gốc rễ: tài liệu chưa phân biệt ba loại bản ghi có vòng đời khác nhau.
- **Mức độ**: Blocker (ảnh hưởng thiết kế dữ liệu của CMD/ECO/GFT/DAT ngay từ P0)
- **Đề xuất**: Thêm vào glossary và một yêu cầu mới (ví dụ CMD-08) định nghĩa ba bản ghi:
  - *Idempotency entry*: ánh xạ key → kết quả đã trả; có TTL = replay window (giá trị do TBD mới, xem F-E06); hết hạn thì retry cùng key trả mã "ngoài replay window" (không phải lỗi nghiệp vụ, không thực thi lại).
  - *Receipt*: bản ghi kết quả nghiệp vụ đã commit, gắn nguyên tử với ledger (CMD-03), **không TTL**, tra cứu được qua Query receipt theo `receiptId`/`requestId` bởi player sở hữu và GM có quyền.
  - *Ledger entry*: delta resource (ECO-02/ECO-06), không TTL (DAT-03).
  - Viết lại GFT-05: "Receipt claim phải lưu `rewardSnapshot` và `giftcodeConfigVersion` tại lúc claim; receipt không bị xóa theo retention của idempotency entry."
  - Viết lại DAT-03: "TTL chỉ áp dụng cho session và idempotency entry; receipt, ledger, progression, resource không TTL."

### F-A03 — Atomicity đa-document quy định ở 5 chỗ với 4 cách gọi khác nhau, trong khi TBD-07 chưa chốt
- **Liên quan**: ECO-04, DAT-04, JOB-02, SEC-14, GFT-03, CMD-03, ACC-07, TBD-07
- **Vấn đề**: ECO-04 nói "transaction boundary hoặc durable workflow được công bố"; DAT-04 "transaction hoặc durable workflow"; JOB-02 "durable workflow/outbox hoặc tương đương"; SEC-14 "cùng commit hoặc durable outbox trong cùng commit" (câu này tự mâu thuẫn: outbox *trong cùng commit* với kết quả nghiệp vụ tức là vẫn là một commit); GFT-03 "nhất quán" không nói cơ chế. Người thiết kế không biết đâu là rule gốc, đâu là áp dụng. Mỗi chỗ lại có một ngoại lệ riêng (JOB-02 cho phép in-memory khi chấp nhận mất; SEC-14 không cho phép).
- **Mức độ**: Major
- **Đề xuất**: Gom thành một yêu cầu gốc trong mục 12 (ví dụ DAT-04 mở rộng): "Mọi command làm thay đổi ≥ 2 document phải chạy trong một trong hai cơ chế: (a) Couchbase transaction; (b) durable workflow/outbox có bước ghi đầu tiên nằm trong cùng commit với state chính. Cơ chế áp dụng cho từng command được ghi trong Published Contract Register (xem F-C01). Lựa chọn giữa (a)/(b) theo nhóm command là TBD-07." Sau đó ECO-04, JOB-02, SEC-14, GFT-03 chỉ ghi "theo DAT-04" cộng điểm riêng (ví dụ SEC-14: audit record thuộc tập document trong cùng boundary).

### F-A04 — "Revision" mang hai vai trò (CAS concurrency vs version client nhìn thấy) và scope "phải được công bố" ở 5 chỗ nhưng không ai định nghĩa
- **Liên quan**: glossary "Revision", API-08, IDN-07, PLY-02, CMD-05, mục 12 đoạn cuối ("không coi revision riêng của resource là global revision")
- **Vấn đề**: CMD-05 dùng revision làm cơ chế bảo vệ writer (CAS); API-08 dùng revision để client phát hiện "sai revision" và resync; mục 12 nói revision là per-document, không global. Nếu snapshot login gồm nhiều document (profile, progression, resource) thì client nhận nhiều revision hay một? Khi nào client coi là "sai revision"? Câu hỏi này quyết định shape của mọi response Protobuf nhưng tài liệu chỉ nói "theo scope được công bố" — và không nơi nào công bố.
- **Mức độ**: Major
- **Đề xuất**:
  - Glossary: *Revision (document)* — số tăng đơn điệu của một document, dùng cho CAS (CMD-05), không lộ ra client. *State version (player)* — giá trị trả về client trong snapshot/delta (API-08), scope = toàn bộ player state hoặc theo từng nhóm được liệt kê. Chọn một trong hai mô hình là một TBD mới (F-E06).
  - API-08 viết lại: "Mỗi response command trả `stateVersion` theo scope S (S liệt kê trong Published Contract Register). Client resync bằng Query snapshot khi `stateVersion` nhận được không liên tiếp với bản client đang giữ."

### F-A05 — Audit chồng lên ledger và receipt; quan hệ ba bản ghi không được nêu
- **Liên quan**: SEC-14, ECO-06, GM-03, mục 13 đoạn cuối, LOG-06
- **Vấn đề**: Một GM resource adjustment theo tài liệu tạo: ledger entry (ECO-06), receipt (ECO-06 "có reason và receipt"), audit record (SEC-14, GM-03). Ba bản ghi, ba nơi quy định, không nói bản nào là nguồn sự thật, có trùng dữ liệu không (before/after trong audit vs delta trong ledger), và GM sửa *progression* (GM-03) thì không có ledger — audit before/after là bản ghi duy nhất. SEC-14 chỉ áp cho "mutation GM"; audit cho command player (ai, khi nào, kết quả) nằm ở receipt hay không có?
- **Mức độ**: Major
- **Đề xuất**: Thêm bảng "Bản ghi bền vững" trong mục 13: loại bản ghi | sinh ra bởi | nội dung tối thiểu | retention | người đọc. Quy định: audit record là bắt buộc cho mọi mutation có actor = GM (kể cả thay đổi quyền, giftcode definition); receipt là bắt buộc cho mọi command có actor = Player hoặc GM thay đổi resource/progression; ledger là bắt buộc cho mọi delta resource bất kể actor. Audit tham chiếu `receiptId` thay vì chép lại delta.

### F-A06 — Multi-game: MVP "một game một deployment" nhưng key/permission/idempotency phải có game/environment "khi có thể va chạm"
- **Liên quan**: mục 2.3 đoạn cuối, CMD-01, DAT-01, SEC-07, ACC-10, mục 12 "trường nền tảng"
- **Vấn đề**: "Khi có thể va chạm" là điều kiện không kiểm chứng được tại thời điểm thiết kế; nếu MVP là một game thì không có va chạm → đội có thể bỏ `gameId` khỏi key, và sau này gắn vào là migration toàn bộ dữ liệu. Ngược lại SEC-07 và ACC-10 đòi GM scope theo game/environment ngay P0. Thêm nữa "environment" trong CMD-01/DAT-01 không rõ là Development/Staging/Production (OPS-01 — vốn là deployment tách biệt, không chung database) hay một logical environment trong cùng deployment.
- **Mức độ**: Major
- **Đề xuất**: Chốt bằng một yêu cầu mới (ví dụ ARC-09): "`gameId` và `environmentId` là một phần bắt buộc của actor context, của mọi document key và của mọi permission scope từ P0, kể cả khi deployment chỉ phục vụ một game." Glossary: *Environment* = logical partition trong một deployment (ví dụ `prod`, `qa-sandbox`), khác với *Deployment environment* ở OPS-01. Vị trí truyền `gameId` (route/header/claim) là TBD mới (F-E06).

### F-A07 — Điều kiện hoàn thành P0 tham chiếu "các ACC áp dụng cho baseline" nhưng không liệt kê; ACC-13/14/15 kéo module P1/P2 vào P0
- **Liên quan**: mục 16 (hàng P0, đoạn "P0 phải chứng minh kiến trúc mở rộng bằng module/policy mẫu nhỏ"), EXT-05, ACC-13, ACC-14, ACC-15, mục 9.2 đoạn cuối
- **Vấn đề**: ACC-13 cần "module Event mẫu"; ACC-14 cần "Leaderboard mặc định bên dưới League mẫu" — Leaderboard là P1, League là P2 (mục 9.2, mục 16). ACC-15 kiểm job rollover — JOB-01 là P1. Mục 16 nói P0 "không bị mặc định mở rộng thành Event/Leaderboard/League" nhưng EXT-05 yêu cầu game mẫu chứng minh "dùng service mà không expose API mặc định" — ví dụ duy nhất trong tài liệu là Leaderboard→League (5.2). Như vậy không có cách khách quan để tuyên bố P0 xong.
- **Mức độ**: Blocker (chặn đóng P0)
- **Đề xuất**: Thêm cột **Phase** vào bảng ACC (mục 15). Viết lại ACC-13 cho P0: "Thêm một module mẫu tối thiểu (`Sample.Counter` hoặc tương đương: 1 command, 1 query, 1 policy thay được) và thay policy của nó" — không dùng Event. Chuyển ACC-14 sang P1 (khi Leaderboard tồn tại), ACC-15 sang P1. P0 hàng "Điều kiện hoàn thành" liệt kê tường minh: ACC-01..ACC-13, ACC-17, ACC-18.

### F-A08 — "Giftcode phải đi qua Economy" lặp 3 lần
- **Liên quan**: mục 2.1 (đoạn dưới bảng), ECO-01, GFT-06
- **Vấn đề**: Ba câu cùng ý; GFT-06 còn thêm "Unity chỉ hiển thị kết quả authoritative" — vốn là PLY-01.
- **Mức độ**: Minor
- **Đề xuất**: Giữ ECO-01 làm gốc; GFT-06 đổi thành "Reward giftcode là một resource grant theo ECO-01..ECO-06"; mục 2.1 chỉ giữ câu lịch sử "thay thế thiết kế backup cũ".

### F-A09 — Session của player: cơ chế truyền không được nêu; "cơ chế authentication phù hợp" là câu duy nhất
- **Liên quan**: mục 6 đoạn sau bảng API, IDN-07, IDN-08, IDN-09, SEC-02, SEC-04 (chỉ GM), OPS-04 (chỉ cookie)
- **Vấn đề**: GM có mô tả cookie/HttpOnly/CSRF chi tiết; Player chỉ có "phải nằm trong cơ chế authentication phù hợp". Token dạng gì, truyền qua header hay field Protobuf, sống bao lâu, refresh thế nào — IDN-08 chỉ nói "phải có". ACC-11 kiểm expiry/revocation chỉ cho cookie (GM); không có ACC cho player session.
- **Mức độ**: Minor về mâu thuẫn, nhưng là gap kiểm chứng (xem F-C04)
- **Đề xuất**: Thêm IDN-10: "Player session được truyền trong HTTP header `Authorization`; không nằm trong Protobuf body. Access token có expiry ≤ X; refresh token/re-auth theo TBD-04. Thu hồi có hiệu lực trên mọi instance trong ≤ Y giây." X, Y vào TBD.

### F-A10 — Ánh xạ HTTP status cho các tình huống nghiệp vụ không khớp
- **Liên quan**: API-05, API-04, IDN-06, CMD-02, CMD-04, GFT-04
- **Vấn đề**: API-05 liệt kê 409 cho "conflict" nhưng không nói account conflict (IDN-06), key reuse với payload khác (CMD-02), commit chưa rõ (CMD-04 "mã có thể tra cứu/retry"), hết quota (GFT-04) dùng status nào. Sniper PVP hiện phân biệt lỗi bằng error code trong body; tài liệu chưa quyết định status là phân loại thô còn `code` mới là ngữ nghĩa hay ngược lại.
- **Mức độ**: Minor
- **Đề xuất**: Thêm vào API-05: "HTTP status chỉ phân loại; ngữ nghĩa nghiệp vụ nằm ở `code` (API-04). Bảng ánh xạ bắt buộc: IDN-06 → 409 + `IDENTITY_CONFLICT`; CMD-02 → 409 + `IDEMPOTENCY_PAYLOAD_MISMATCH`; CMD-04 → 503/409 + `COMMIT_UNKNOWN_RETRY`; GFT-04 quota → 200 + `GIFTCODE_QUOTA_EXCEEDED` (lỗi nghiệp vụ, không phải lỗi giao thức)." Bảng đầy đủ thuộc error-code catalog (F-B22).

### F-A11 — Ba loại "migration/compatibility" ở ARC-08, OPS-06, DAT-02 không cross-reference
- **Liên quan**: ARC-08 (package/framework), OPS-06 (API/protocol/schema deploy), DAT-02 (data schema), API-03 (proto field)
- **Vấn đề**: Bốn yêu cầu nói về tương thích theo bốn lớp khác nhau, không nối nhau; người đọc không biết "schema" ở OPS-06 là proto schema hay document schema.
- **Mức độ**: Minor
- **Đề xuất**: Thêm một đoạn "Ma trận tương thích" trong mục 14: lớp (package / wire proto / document schema / GM JSON) | yêu cầu gốc | ai sở hữu | kiểm bằng ACC nào.

### F-A12 — Durability policy tham chiếu 3 chỗ, chưa nơi nào chọn
- **Liên quan**: ECO-05 ("durability policy đã chọn"), DAT-05 ("công bố theo operation"), TBD-07
- **Vấn đề**: ECO-05 giả định đã chọn; DAT-05 giả định sẽ công bố; TBD-07 nói chưa chốt. Không mâu thuẫn logic nhưng người đọc ECO-05 tưởng đã có.
- **Mức độ**: Minor
- **Đề xuất**: ECO-05 sửa "theo durability policy của operation ghi trong Published Contract Register (DAT-05), giá trị mặc định chốt tại TBD-07".

### F-A13 — Health endpoint vừa là Public (SEC-09) vừa phản ánh dependency (OBS-03)
- **Liên quan**: SEC-09, OBS-03
- **Vấn đề**: Readiness "phản ánh dependency cần thiết" nếu public sẽ lộ trạng thái Couchbase ra Internet. Tài liệu không nói readiness là Public hay chỉ cho orchestrator/LB.
- **Mức độ**: Minor
- **Đề xuất**: OBS-03 thêm: "Liveness/readiness chỉ trả trạng thái tổng (200/503), không trả chi tiết dependency ở security profile Public; chi tiết chỉ qua endpoint GM hoặc network nội bộ."

### F-A14 — MUST/SHOULD được định nghĩa ở mục 3 nhưng hầu hết yêu cầu viết "phải"; SHOULD chỉ xuất hiện ẩn
- **Liên quan**: mục 3, SEC-03 ("Ưu tiên"), OPS-02 ("đề xuất"), PERF-07 ("ưu tiên"), LOG-02 ("đề xuất"), 2.2 "baseline đề xuất"
- **Vấn đề**: Không thể biết yêu cầu nào được phép đánh đổi khi thiếu thời gian. Ví dụ SEC-03 OIDC/SSO là SHOULD theo từ "ưu tiên" nhưng SEC-10 (thu hồi quyền staff) là MUST — nếu bỏ SSO thì ai thu hồi?
- **Mức độ**: Minor (nhưng là điều kiện để làm F-D01)
- **Đề xuất**: Xem F-F01 — thêm cột Priority; rà toàn bộ và hạ xuống SHOULD những yêu cầu có từ "ưu tiên/đề xuất/nếu phù hợp".

### F-A15 — "Tìm lại cùng player" (2.1) không có yêu cầu IDN tương ứng cho flow reinstall
- **Liên quan**: mục 2.1 hàng Identity, mục 7 đoạn cuối, IDN-03, IDN-05, IDN-06, ACC-01
- **Vấn đề**: Scope nói provider link để "tìm lại cùng player". Mục 7 nói mất guest credential thì không recovery bằng local ID. Nhưng không có IDN-xx nói: "login bằng provider identity đã map phải trả về đúng player, kể cả khi thiết bị mới/không còn guest credential" — đây chính là use case recovery quan trọng nhất (Sniper PVP đã có bài học với Google account link và WELCOME BACK). ACC-01 chỉ kiểm "login provider hợp lệ" chung chung. Cũng không nói: thiết bị mới có guest mới + login provider đã map → guest mới bị bỏ hay giữ (IDN-06 chỉ xử lý link, không xử lý login).
- **Mức độ**: Major
- **Đề xuất**: Thêm IDN-11: "Login bằng provider identity đã map phải trả về player đã map, độc lập với local ID/guest credential của thiết bị. Nếu thiết bị đang có session guest chưa link, server trả `SWITCH_ACCOUNT` để client xác nhận chuyển; không tự merge (2.3)." Thêm ACC tương ứng: "Cài lại app, login Google đã link → nhận đúng player cũ".

### F-A16 — Yêu cầu dành cho client (PERF-06, API-08 resync) nằm trong tài liệu server; ACC-18 kiểm client nhưng client SDK không phải deliverable rõ ràng
- **Liên quan**: PERF-06, API-08, ACC-18, deliverable 3 ("Unity integration sample")
- **Vấn đề**: "Client retry dùng exponential backoff/jitter" và "client có cơ chế resync" là hành vi client; nếu framework chỉ bàn giao "sample" thì mỗi game tự viết và ACC-18 không có đối tượng kiểm thử cố định.
- **Mức độ**: Minor (mâu thuẫn), Major xét như gap — xem F-B08
- **Đề xuất**: Tách mục "Yêu cầu đối với client SDK" (CLI-01..) và ghi rõ chủ sở hữu.

---

## 2. Thiếu sót (gap)

Nguyên tắc đánh giá: một gap chỉ được tính khi (a) game live loại này gần như chắc chắn cần, và (b) tài liệu không nhắc hoặc không tuyên bố out-of-scope tường minh. Với mỗi gap, đề xuất gồm cả phương án "chỉ cần ghi out-of-scope" nếu hợp lý — mục tiêu là tài liệu phải *quyết định*, không nhất thiết phải *làm*.

### F-B01 — Remote config / game configuration service chỉ được nhắc qua "version" ở PLY-03
- **Liên quan**: PLY-03, GFT-05 ("config version"), mục 5.1 mức "Configuration", mục 12 nhóm dữ liệu tối thiểu
- **Vấn đề**: Mọi rule server-side (chi phí nâng cấp, bảng reward, cooldown) cần một nguồn config có version, có môi trường, có cách GM/LiveOps sửa và client đọc. Tài liệu chỉ nói "phải có version". Không nói: config lưu ở đâu (file deploy cùng binary, document Couchbase, GM page), ai được sửa, client lấy qua API nào, config version có nằm trong receipt không (GFT-05 nói có cho giftcode, PLY-05 không nói cho command thường). Sniper PVP hiện dựa Firebase RemoteConfig + PlayFab title data — nếu framework không có thay thế thì game mới vẫn phải phụ thuộc dịch vụ ngoài.
- **Mức độ**: Major
- **Đề xuất**: Thêm nhóm yêu cầu CFG-01..03: (1) Game config là tập document có `configVersion`, scope game/environment, chỉ đọc từ Player API; (2) Mọi receipt ghi `configVersion` đã dùng để tính kết quả; (3) Cách cập nhật config (deploy-time ở P0; GM page ở P1) và cách client biết config đổi (qua `stateVersion`/login). Nếu team quyết định dùng dịch vụ ngoài → ghi vào 2.3 kèm điều kiện "server vẫn phải đọc cùng một bản config để tính kết quả".

### F-B02 — Item catalog / resource type registry không tồn tại, nhưng GFT-01 đã tham chiếu `itemId`
- **Liên quan**: glossary "Resource", GFT-01 `{type, itemId, quantity}`, ECO-02 ("vượt giới hạn của game"), GM-03, GM-04
- **Vấn đề**: Không có yêu cầu nào nói resource type/item được định nghĩa ở đâu, ai sở hữu (framework hay game), server validate `itemId` tồn tại thế nào, item có stack hay instance (equipment có level/rarity riêng), giới hạn tối đa mỗi loại. GM-04 "định nghĩa reward" sẽ cần dropdown danh sách item — lấy từ đâu?
- **Mức độ**: Major
- **Đề xuất**: Thêm ECO-07: "Game phải đăng ký Resource Catalog (resourceType, itemId, stackable/instanced, maxQuantity, displayName) qua contract của Economy; mọi grant/debit, giftcode reward và GM adjustment bị từ chối nếu `itemId` không có trong catalog." Instanced item (inventory có thuộc tính riêng) ghi rõ là P1 hoặc out-of-scope.

### F-B03 — IAP receipt validation và grant qua Economy không được nhắc
- **Liên quan**: ECO-01, ECO-06, mục 2.3 (không liệt kê)
- **Vấn đề**: Mọi game mobile của team có IAP; server-side receipt validation (Google Play / App Store) là đường grant resource quan trọng nhất về tiền và là chỗ hay bị lạm dụng nhất. Tài liệu không nhắc cả trong scope lẫn out-of-scope. Nếu để game tự làm, mỗi game sẽ có một đường ghi resource lách ECO-01.
- **Mức độ**: Major
- **Đề xuất**: Hoặc thêm vào 2.3 "IAP validation: out-of-scope P0; P1 cung cấp module IAP (validate receipt theo store, chống dùng lại receipt, grant qua Economy với receipt và ledger)". Hoặc ít nhất thêm ECO-08: "Mọi grant từ nguồn ngoài (IAP, ads reward) phải đi qua ECO-01 với `sourceType` và `externalReference` duy nhất để chống grant hai lần."

### F-B04 — Maintenance mode, version gate và force update không có
- **Liên quan**: OPS-06 (chỉ nói deploy tương thích), API-02 (version endpoint), SEC-09
- **Vấn đề**: Để deploy an toàn (OPS-06), rollback (ACC-19) và chặn client quá cũ (API-03/API-09), server cần: (1) bật maintenance mode trả mã riêng cho Player API nhưng GM vẫn vào được; (2) min supported client version theo platform; (3) thông điệp/URL update. Thiếu cái này thì OPS-06 không thực thi được.
- **Mức độ**: Major
- **Đề xuất**: Thêm OPS-08: "Có maintenance flag theo game/environment do GM bật/tắt với quyền riêng; khi bật, Player API trả `MAINTENANCE` kèm thời gian dự kiến, Public login/health và GM API vẫn hoạt động." Thêm API-10: "Mọi Player request gửi `clientVersion` + `platform`; server trả `UPDATE_REQUIRED` khi thấp hơn min version cấu hình." ACC mới: "Bật maintenance → player bị chặn, GM vào được; hạ min version → client cũ bị chặn trước xử lý nghiệp vụ."

### F-B05 — Account deletion và data export bị đẩy cho "scope game" dù là yêu cầu tuân thủ store
- **Liên quan**: mục 7 đoạn cuối, TBD-04, TBD-08 (PII policy)
- **Vấn đề**: Apple và Google yêu cầu app có account creation phải cung cấp account deletion trong app. Đây là ràng buộc nền tảng, không phải lựa chọn của từng game, nên framework phải có capability. Data export (GDPR) có thể ít cấp thiết hơn nhưng cần quyết định.
- **Mức độ**: Major
- **Đề xuất**: Thêm IDN-12: "Framework cung cấp command xóa tài khoản do player khởi tạo: thu hồi session, xóa/ẩn identity mapping, đánh dấu player `deleted` với grace period X ngày, sau đó purge progression/resource; ledger/receipt/audit giữ theo TBD-08 nhưng bỏ PII." GM có quyền xem trạng thái và hủy trong grace period. Data export: ghi tường minh P1 hoặc out-of-scope.

### F-B06 — Ban / suspend / anti-abuse không có yêu cầu
- **Liên quan**: GM-02..GM-06 (không có hành động ban), SEC-13 (chỉ rate limit), IDN-09
- **Vấn đề**: GM tool không có thao tác khóa tài khoản; Player API không có check trạng thái ban tại login và tại command. Đây là thao tác GM phổ biến nhất sau xem/sửa resource.
- **Mức độ**: Major
- **Đề xuất**: Thêm GM-07: "Ban/suspend/unban player với reason, thời hạn, phạm vi (login / một nhóm command), có audit; trạng thái được kiểm tại login và trước mọi command (IDN-09)." Permission `player.ban` thêm vào SEC-07. ACC mới: "Player bị ban → login trả `ACCOUNT_SUSPENDED`; session hiện có bị thu hồi trong ≤ Y giây".

### F-B07 — Không có server time API
- **Liên quan**: GFT-02, EVT-01 (lịch UTC), PLY-04 (cooldown)
- **Vấn đề**: Server không tin thời gian client (đúng), nhưng client cần hiển thị countdown cooldown/event. Không có yêu cầu trả `serverTimeUtc` trong response hoặc endpoint riêng.
- **Mức độ**: Minor
- **Đề xuất**: API-11: "Mọi response Player API mang `serverTimeUtc`; Core cung cấp `IClock` dùng thống nhất (mục 4.1 đã có 'clock')."

### F-B08 — Client SDK Unity: không rõ ai sở hữu, phạm vi gì, Unity version nào
- **Liên quan**: API-09, API-08 (resync), PERF-06 (retry), ACC-18, deliverable 3
- **Vấn đề**: Deliverable chỉ là "generated-client workflow và Unity integration sample". Nhưng retry với cùng requestId, backoff/jitter, resync theo stateVersion, xử lý `MAINTENANCE`/`UPDATE_REQUIRED`, gắn session header — là logic không tầm thường và nếu mỗi game tự viết thì ACC-18 không có đối tượng kiểm. Sniper PVP đã có chokepoint `ServerAPI.GameAPI.CallAPI<T>`; framework nên có tương đương. Cũng không nêu Unity LTS version, scripting backend, min OS.
- **Mức độ**: Major
- **Đề xuất**: Thêm mục "Client SDK" với CLI-01..04: (1) package Unity (UPM) do framework sở hữu, versioned cùng proto; (2) chịu trách nhiệm transport, session header, requestId, retry/backoff, resync, error mapping; (3) target: Unity <version> LTS, IL2CPP Android/iOS, stripping High; (4) sample game dùng SDK. Deliverable 3 sửa tương ứng.

### F-B09 — Không có yêu cầu về test strategy, CI và tự động hóa ACC
- **Liên quan**: mục 15 đoạn cuối (chỉ nói cần integration env), ARC-01 ("unit test use case"), deliverable 6
- **Vấn đề**: Tài liệu nói mock không thay được integration test nhưng không nói: ACC nào phải tự động, CI chạy gì trước merge, integration env Couchbase ai cung cấp/ở đâu, contract test cho `.proto` (API-03 reserved field) kiểm thế nào, coverage tối thiểu cho Core/Application. Với framework dùng chung nhiều game, regression test là tài sản chính.
- **Mức độ**: Major
- **Đề xuất**: Thêm mục 15.1 "Chiến lược kiểm thử": (1) unit test cho Core/Application không cần DB (ARC-01); (2) integration test chạy Couchbase thật (container) trong CI cho ACC-04..08; (3) proto compatibility check tự động (field number/reserved) trên mỗi PR; (4) load test theo 11.1 chạy theo lịch, không chạy trên PR; (5) ACC-xx nào thủ công thì ghi rõ. Deliverable 6 thêm "test suite chạy được trong CI".

### F-B10 — Deliverable tài liệu thiếu API reference, error-code catalog, ADR
- **Liên quan**: deliverable 2, 4, 5; API-04
- **Vấn đề**: Có "hướng dẫn đăng ký/override" và "permission catalog" nhưng không có: OpenAPI/JSON schema cho GM API (frontend cần), tài liệu `.proto` sinh tự động, danh mục error code, và nơi ghi quyết định thiết kế (ADR) — "Published Contract Register" (F-C01) cũng là một deliverable tài liệu.
- **Mức độ**: Minor
- **Đề xuất**: Deliverable 8: "API reference (OpenAPI cho GM, proto doc cho Player), error-code catalog, Published Contract Register, ADR log."

### F-B11 — Không có cheat/dev tooling cho QA ở môi trường non-production
- **Liên quan**: OPS-01, SEC-09, GM-03
- **Vấn đề**: QA cần: set resource tùy ý, reset player, dịch server time (test event/cooldown/giftcode hết hạn), ép trạng thái progression. GM-03 là đường chính thức nhưng chậm (audit, reason) và không có time-travel. Nếu không quy định, dev sẽ thêm endpoint ẩn — vi phạm SEC-08/SEC-09. Sniper PVP đã phải xây hệ thống Cheat với guard `USE_CHEAT && !ENSURE_NO_CHEAT`.
- **Mức độ**: Major
- **Đề xuất**: Thêm OPS-09: "Có security profile `Dev` cho endpoint cheat; chỉ đăng ký khi `environment != Production` (fail startup nếu vi phạm); gồm set resource, reset player, offset clock theo player/environment; mọi cheat vẫn ghi ledger với `sourceType = cheat`." ACC: "Build Production với module cheat → startup fail."

### F-B12 — `gameId`/`environmentId` đi vào hệ thống bằng đường nào không được quy định
- **Liên quan**: F-A06, mục 6 đoạn cuối ("`gameId` do client đưa vào payload không được tin"), SEC-07, DAT-01, CMD-01
- **Vấn đề**: Tài liệu nói client không được tự khai `gameId` nhưng không nói server lấy từ đâu: route prefix, host name, claim trong session token, cấu hình deployment, Couchbase scope/collection. Mỗi lựa chọn kéo theo shape khác nhau cho route, key và permission. Đây là quyết định P0 không thể lùi.
- **Mức độ**: Major
- **Đề xuất**: Thêm TBD-11 (F-E06) và yêu cầu ARC-10: "`gameId` được xác định từ deployment config hoặc session claim, không từ request body; `environmentId` từ deployment config. Actor context mang cả hai và mọi repository key/permission check nhận từ actor context."

### F-B13 — Push notification: không nhắc
- **Mức độ**: Minor
- **Đề xuất**: Ghi vào 2.3 "out-of-scope; game tích hợp FCM/APNs phía client".

### F-B14 — A/B testing và feature flag: không nhắc
- **Liên quan**: F-B01, mục 5.1 mức Configuration
- **Mức độ**: Minor
- **Đề xuất**: Ghi là P1 cùng CFG (segment theo player → config variant) hoặc out-of-scope tường minh.

### F-B15 — Inbox/mail để trả quà bù (compensation) không có; GM-03 chỉ cộng thẳng resource
- **Mức độ**: Minor
- **Đề xuất**: Ghi P1 hoặc out-of-scope; nếu out-of-scope, GM-03 cần nói rõ player nhận delta qua `stateVersion` resync chứ không có thông báo.

### F-B16 — Player profile (display name, avatar, validation, uniqueness) có trong nhóm dữ liệu mục 12 nhưng không có yêu cầu
- **Liên quan**: mục 12 "player profile", GM-02
- **Mức độ**: Minor
- **Đề xuất**: Thêm PRF-01 tối thiểu: display name do server validate (độ dài, ký tự, profanity hook là policy của game), không bắt buộc unique ở P0.

### F-B17 — Cách client "tra cứu receipt" (CMD-04) không được định hình
- **Liên quan**: CMD-04, CMD-07, API-07
- **Vấn đề**: Không rõ client retry bằng cách gửi lại nguyên command (server trả receipt cũ) hay có Query `GetReceipt(requestId)`. Hai cách có hệ quả khác nhau cho idempotency entry TTL và cho client SDK.
- **Mức độ**: Minor
- **Đề xuất**: Chốt trong CMD-04: "Cơ chế tra cứu = gửi lại nguyên command với cùng `requestId` (server trả receipt đã commit); ngoài ra có Query `GetReceipt` cho GM và cho client sau khi mất replay window."

### F-B18 — Game analytics/telemetry event (không phải ops metrics): không nhắc
- **Mức độ**: Minor
- **Đề xuất**: Ghi vào 2.3 là out-of-scope (client gửi AppsFlyer/Firebase) hoặc P1 "server-side event sink".

### F-B19 — Không nói framework có phục vụ di dời Sniper PVP khỏi PlayFab hay chỉ cho game mới
- **Liên quan**: mục 1, mục 2.3, deliverable
- **Vấn đề**: Nếu một game đang live sẽ migrate, cần data import tool, dual-run, mapping identity từ PlayFab ID — effort lớn hơn cả Identity module. Nếu chỉ cho game mới, cần nói rõ để không ai đặt giả định ngược.
- **Mức độ**: Major
- **Đề xuất**: Thêm vào mục 2.3: "Di dời dữ liệu từ backend hiện hữu (PlayFab) không thuộc phạm vi tài liệu này; nếu phát sinh sẽ có tài liệu riêng." Hoặc ngược lại thêm phase riêng.

### F-B20 — Giftcode: sinh code hàng loạt, code dùng một lần vs code chung, định dạng code
- **Liên quan**: GFT-01, GM-04
- **Mức độ**: Minor
- **Đề xuất**: GFT-01 bổ sung: "Hai loại code: shared (một chuỗi, quota tổng + giới hạn per player) và unique batch (N chuỗi, mỗi chuỗi một lần). Định dạng: không phân biệt hoa thường, tập ký tự không nhầm lẫn." Unique batch có thể P1.

### F-B21 — App attestation / anti-bot baseline không được tuyên bố out-of-scope
- **Liên quan**: mục 2.3 (chỉ nói không cam kết chống gian lận tuyệt đối), SEC-13
- **Mức độ**: Minor
- **Đề xuất**: Thêm vào 2.3: "Play Integrity / App Attest là hook policy của game (P2), không thuộc baseline."

### F-B22 — Error-code catalog: không có yêu cầu về registry chống trùng giữa module
- **Liên quan**: API-04, ARC-07 (duplicate route được phát hiện, nhưng duplicate code thì không)
- **Mức độ**: Minor
- **Đề xuất**: API-04 bổ sung: "Error code có prefix module; registry trung tâm phát hiện trùng khi startup (tương tự ARC-07)."

### F-B23 — Ngưỡng rate limit / in-flight / queue không có chỗ chốt
- **Liên quan**: PERF-04, PERF-05, SEC-13, API-06; bảng 11.1 và TBD-06 không bao gồm
- **Mức độ**: Minor
- **Đề xuất**: Thêm dòng vào bảng 11.1: "Ngưỡng rate limit theo nhóm endpoint và theo actor | TBD | Trước load test".

### F-B24 — Daily reset / timezone của nội dung lặp (daily quest, daily giftcode limit)
- **Liên quan**: GFT-01 (eligibility), EVT-01 (lịch UTC), PLY-04 (cooldown)
- **Mức độ**: Minor
- **Đề xuất**: Thêm câu vào PLY-04 hoặc EVT-01: "Mọi chu kỳ reset tính theo UTC; game muốn reset theo local time phải khai báo offset trong config, server vẫn tính bằng UTC."

---

## 3. Tính kiểm chứng (testability)

### F-C01 — "Được công bố" xuất hiện 14 lần, không nơi nào nói công bố ở đâu, ai, khi nào
- **Liên quan**: glossary "Revision", EXT-01, mục 5.1 hàng Infrastructure, API-06, API-07, API-08, PLY-02, PLY-05, ECO-04, CMD-07, LDB-01, DAT-05, SEC-10, mục 16 hàng P1
- **Vấn đề**: "Công bố" là động từ không có chủ ngữ và địa điểm; reviewer không thể kiểm "đã công bố chưa". Đây là mẫu câu che giấu quyết định chưa có.
- **Mức độ**: Major
- **Đề xuất**: Định nghĩa một deliverable **Published Contract Register** (PCR) — một tài liệu duy nhất, versioned cùng framework, có các mục: limits (API-06), stateVersion scope (API-08), consistency theo nhóm dữ liệu (PLY-02), transaction boundary theo command (ECO-04), replay window (CMD-07), durability theo operation (DAT-05), thời gian thu hồi tối đa (SEC-10), leaderboard semantics (LDB-01). Thay mọi "được công bố" bằng "được ghi trong PCR mục X, chốt trước <mốc>". Thêm ACC: "PCR tồn tại và mỗi mục có giá trị trước nghiệm thu phase tương ứng."

### F-C02 — "Phù hợp" / "thích hợp" / "rõ ràng" / "có nghĩa" xuất hiện ≥ 15 lần, không kiểm được
- **Liên quan**: API-04 ("chỉ dẫn retry nếu phù hợp"), GM-02 ("identity phù hợp"), SEC-04 ("SameSite phù hợp"), SEC-05 ("hành vi chống request giả mạo phù hợp"), SEC-13 ("limits phù hợp"), PERF-06 ("khi phù hợp"), PERF-07 ("index phù hợp"), LOG-03 ("theo policy"), OPS-04 ("thích hợp"), DAT-03 ("phù hợp CMD-07"), mục 2.2 ("được kiểm tra tương thích"), ARC-03 ("điểm vào rõ ràng"), ARC-06 ("lỗi có nghĩa"), CMD-06 ("ordering rõ ràng"), IDN-07 ("rõ ràng"), PERF-08 ("scope rõ ràng")
- **Mức độ**: Major
- **Đề xuất** (mẫu viết lại):
  - SEC-04: "`SameSite=Lax` mặc định; `SameSite=None` chỉ khi khác origin và phải kèm SEC-06."
  - SEC-05: "Logout là POST có antiforgery token; GET logout không được chấp nhận."
  - SEC-13: "Login ≤ N lần/phút/IP và ≤ M lần/phút/device; giá trị N, M trong PCR."
  - ARC-06: "Startup fail với message chứa tên module, tên cấu hình/dependency thiếu và cách khắc phục."
  - PERF-07: "Mọi query trong Player command là key lookup hoặc index đã đăng ký trong module (DAT-01); query không dùng index bị test integration phát hiện qua `EXPLAIN`."
  - LOG-03: "Container phải mount persistent volume cho thư mục log hoặc có sidecar ship log trong ≤ 60 giây; chọn phương án tại OPS runbook."

### F-C03 — Yêu cầu định lượng không có số và không có TBD tương ứng
- **Liên quan**: SEC-10 ("thời gian tối đa đã công bố"), OPS-03 ("drain có thời hạn"), PERF-04 (giới hạn), PERF-05 (budget), API-06 (limits), SEC-13, OBS-03 ("restart storm"), LOG-06 (bounded queue — bao nhiêu)
- **Vấn đề**: Khác với 11.1 (ghi TBD có mốc), những số này không có chỗ chốt → sẽ được chọn ngẫu nhiên trong code.
- **Mức độ**: Major
- **Đề xuất**: Bảng "Tham số vận hành mặc định" (phụ lục) với giá trị đề xuất + mốc chốt, ví dụ: thu hồi quyền GM có hiệu lực ≤ 60 s; drain ≤ 30 s; payload Player ≤ 256 KiB, GM ≤ 2 MiB; page size ≤ 100; log queue 10 000 entries, drop-with-counter khi đầy. Số cụ thể có thể sai, nhưng phải có số để kiểm.

### F-C04 — Khoảng 70% yêu cầu không có ACC; ACC thiếu cột "kiểm cho yêu cầu nào"
- **Liên quan**: mục 15 (19 ACC) so với ~110 yêu cầu có ID
- **Vấn đề**: Các nhóm yêu cầu sau không có ACC nào chạm tới:
  - ARC-02, ARC-05, ARC-06, ARC-08; EXT-01..EXT-04 (ACC-13/14 chỉ chạm một phần)
  - API-01..API-06; API-09 chỉ qua ACC-18 (không nêu target cụ thể)
  - IDN-04, IDN-07, IDN-08 (ACC-11 chỉ kiểm cookie GM — player session expiry/refresh/revoke không có ACC)
  - PLY-02..PLY-06; ECO-02 (overflow/limit), ECO-05; CMD-01, CMD-06, CMD-07
  - GFT-01, GFT-02 (client time bị bỏ qua), GFT-05 (config version pin)
  - GM-01, GM-02, GM-04, GM-05, GM-06 (pagination/limit)
  - SEC-01, SEC-03, SEC-04, SEC-06, SEC-11, SEC-12, SEC-13, SEC-14 (nội dung audit)
  - PERF-01..03, PERF-05, PERF-07..10; DAT-01..03, DAT-05..07; LOG-04..06; OBS-01..03; OPS-01..06
- **Mức độ**: Major
- **Đề xuất**: (1) Thêm cột **Verified by** cho mọi bảng yêu cầu với giá trị thuộc {ACC-xx, Unit, Integration, Review, Load, Runbook drill}. (2) Thêm cột **Covers** vào bảng ACC. (3) Thêm tối thiểu các ACC: player session expiry/refresh/revoke trên 2 instance (IDN-08); ECO-02 overflow và vượt max; GFT-02 client gửi thời gian giả; GFT-05 sửa code sau claim → receipt cũ không đổi; GM-06 query không có pagination bị từ chối; LOG-05 scan log không có token; ARC-06 thiếu config → startup fail có tên module; SEC-14 audit record đủ 7 trường; OPS-03 SIGTERM giữa commit → receipt vẫn có.

### F-C05 — ACC không truy về yêu cầu hoặc phụ thuộc giá trị chưa có
- **Liên quan**: ACC-16 (phụ thuộc toàn bộ 11.1 đang TBD), ACC-17 ("có báo lỗi storage" — không có LOG/OBS yêu cầu alert khi disk lỗi; LOG-06 chỉ nói "hành vi rõ"), ACC-18 (phụ thuộc client SDK chưa có chủ — F-B08), ACC-19 (phụ thuộc TBD-09)
- **Mức độ**: Minor
- **Đề xuất**: Thêm OBS-04: "Lỗi ghi log/disk đầy phải tăng metric `log_write_errors` và health degraded." ACC-16/19 ghi rõ "không chạy được cho tới khi TBD-06/TBD-09 chốt".

### F-C06 — Nhiều MUST nằm trong đoạn văn không có ID
- **Liên quan**: mục 6 đoạn sau bảng (playerId/gameId trong payload không đáng tin); mục 7 đoạn cuối (không recovery từ local ID); mục 5.2 đoạn cuối ("Job chạy lại không được chuyển hạng lần thứ hai", "Player không được tự chọn group"); mục 8.4; mục 10.2 hai đoạn cuối (role vs permission; không dùng shared API key; `/api` vs `/gm` không phải ranh giới bảo mật); mục 12 đoạn "trường nền tảng"; mục 13 đoạn cuối; mục 14 đoạn cuối (tách GM host về sau); mục 15 hai đoạn cuối (integration env; nội dung báo cáo performance)
- **Mức độ**: Minor
- **Đề xuất**: Gán ID cho từng câu "phải/không được" (ví dụ API-12, IDN-13, SEC-15, SEC-16, DAT-09, OPS-10, TST-01, TST-02). Đoạn văn chỉ giữ phần giải thích.

### F-C07 — Chỉ bảng ARC có cột "Tiêu chí kiểm tra"; các bảng khác không
- **Mức độ**: Minor
- **Đề xuất**: Thống nhất cấu trúc cột cho mọi bảng (xem F-F01).

### F-C08 — Mục tiêu 1 và 2 (mục 1) không đo được
- **Liên quan**: mục 1 ("Xử lý I/O bất đồng bộ tốt", "kiến trúc module rõ ràng")
- **Mức độ**: Minor
- **Đề xuất**: Gắn mỗi mục tiêu với ACC: mục tiêu 1 ↔ ACC-16 + 11.1; mục tiêu 2 ↔ ACC-13 (phiên bản P0) + EXT-05; mục tiêu 3 ↔ ACC-09..ACC-12.

### F-C09 — API-09 "tương thích Unity target" không nêu Unity version, scripting backend, platform list
- **Mức độ**: Minor
- **Đề xuất**: "Unity <X> LTS, IL2CPP, Managed Stripping Level High, Android arm64 + iOS arm64, kiểm trên ≥ 1 thiết bị thật mỗi platform." Gắn vào CLI-03 (F-B08).

---

## 4. Phân kỳ P0/P1/P2

### 4.1. Ước lượng tương đối theo module

Thang điểm độ phức tạp 1–5 (1 = vài ngày một người, 5 = nhiều tuần, nhiều tích hợp ngoài). Chỉ là ước lượng tương đối để so tỷ trọng giữa phase, không phải estimate cam kết.

| # | Hạng mục P0 theo mục 16 | Yêu cầu chính | Độ phức tạp | Ghi chú |
| --- | --- | --- | --- | --- |
| 1 | Core + Host + đăng ký module + startup validation | ARC-01..07, SEC-08, SEC-09 | 3 | Nền của mọi thứ khác |
| 2 | Platform.Web: Protobuf + JSON, chuẩn lỗi, security profile, limits, rate limit | API-01..07, API-09, PERF-04, SEC-12, SEC-13 | 4 | Hai wire format, hai auth scheme |
| 3 | Platform.Couchbase: kết nối, CAS, transaction, migration, index provisioning | PERF-03, DAT-01..07, CMD-05 | 4 | Phụ thuộc TBD-07 |
| 4 | Observability: file rolling, metrics, correlation, health | LOG-01..06, OBS-01..03 | 2 | |
| 5 | Identity: guest credential + Google + Facebook + link/conflict + session lifecycle | IDN-01..09 | 5 | Hai provider ngoài, race link, TBD-03/04 |
| 6 | Idempotency / receipt / ledger infrastructure | CMD-01..07, ECO-06 | 4 | Phụ thuộc F-A02 |
| 7 | Economy: catalog, debit/credit, invariant | ECO-01..05 | 3 | |
| 8 | Progression: schema version, command pipeline, validator, command mẫu | PLY-01..06, 8.4 | 3 | Phụ thuộc TBD-01/02 |
| 9 | Giftcode: definition, quota hot-key, claim, history | GFT-01..06 | 3 | Hot-key quota là bài toán riêng |
| 10 | GM backend: OIDC/SSO, cookie, CSRF, CORS, permission + scope, audit | SEC-02..07, SEC-10, SEC-11, SEC-14, GM-06 | 4 | Phụ thuộc TBD-05 |
| 11 | GM frontend xây mới: login, player search, resource/progression edit, giftcode CRUD, permission-driven menu | GM-01..05 | 4 | Một ứng dụng web riêng |
| 12 | Extension proof: template module, game mẫu, (theo ACC-13/14 hiện tại) Event mẫu + Leaderboard + League mẫu | EXT-01..05, ACC-13, ACC-14 | 4 | Kéo P1/P2 vào P0 (F-A07) |
| 13 | Unity integration sample + IL2CPP verification | API-09, ACC-18 | 2 | Thiếu chủ SDK (F-B08) |
| 14 | Ops: env config, graceful shutdown, Data Protection keys, deploy guide, runbook, restore drill | OPS-01..07, ACC-19 | 3 | Phụ thuộc TBD-09 |
| 15 | Performance harness + capacity sign-off | 11.1, PERF-05, ACC-16 | 3 | Phụ thuộc TBD-06 |
| 16 | Integration test env (Couchbase nhiều instance) + CI | mục 15 đoạn cuối | 2 | Chưa có yêu cầu (F-B09) |
| | **Tổng P0** | | **53** | |
| | P1: Event, Leaderboard, jobs/outbox, GM pages | EVT-01, LDB-01..03, JOB-01..02, GM-05 | ~11 | |
| | P2: League hoàn chỉnh, rule đặc thù | 5.2 | ~5 | |

**Kết luận**: P0 ≈ 53/69 ≈ 77% tổng effort. Với đội đang đồng thời vận hành một game live, P0 theo định nghĩa hiện tại là một chương trình nhiều tháng không có mốc giá trị trung gian nào. Ngoài ra, bốn hạng mục P0 (3, 5, 10, 15) phụ thuộc TBD chưa chốt.

### F-D01 — P0 quá nặng, không có vertical slice sớm, và chứa các tích hợp ngoài có lead time không kiểm soát được
- **Liên quan**: mục 16 hàng P0; các hạng mục 5, 10, 11, 12, 15 ở bảng trên
- **Mức độ**: Blocker (về khả thi scope)
- **Rủi ro nếu giữ nguyên**:
  1. Quyết định kiến trúc (revision scope, transaction boundary, module registration) không được kiểm bằng game thật cho tới cuối P0 → rework đắt.
  2. GM frontend xây mới (hạng mục 11) cạnh tranh nhân lực với core trong cùng phase.
  3. Google/Facebook/OIDC provider cần app ID, tài khoản, review của bên thứ ba — không nằm trong tay backend; một trong ba chậm là P0 không đóng.
  4. ACC-16 và ACC-19 là điều kiện P0 nhưng phụ thuộc TBD-06/TBD-09 "trước production" — P0 không thể đóng nếu production chưa được định nghĩa.
  5. Không có điểm dừng để game đầu tiên bắt đầu tích hợp; team game chờ.
- **Đề xuất**: Tách P0 thành **P0 (MVP vertical slice)** và **P0.5 (production hardening)** như bảng dưới; P0 phải kết thúc bằng một game mẫu chạy end-to-end trên môi trường Development.

#### P0 tối thiểu đề xuất (MVP vertical slice)

| Giữ trong P0 | Yêu cầu | Lý do |
| --- | --- | --- |
| Core/Host/đăng ký module + startup validation | ARC-01..07, SEC-08, SEC-09 | Không thể lùi |
| Platform.Web Protobuf + JSON, chuẩn lỗi, status map, size limit | API-01..07, API-09, SEC-12 | Shape wire contract phải ổn định sớm |
| Couchbase + CAS + idempotency + receipt + ledger (một boundary đã chốt ở TBD-07) | CMD-01..05, CMD-07, ECO-06, DAT-01, DAT-03, DAT-06 | Lõi authoritative |
| Identity: **guest only** + session issue/expiry/revoke | IDN-01, IDN-02, IDN-07, IDN-08, IDN-09, IDN-10 (mới) | Đủ để chơi; provider là tích hợp ngoài |
| Progression + Economy với **một** command mẫu (battle complete) + resource catalog | PLY-01..06, ECO-01..05, ECO-07 (mới), 8.4 | Chứng minh pipeline command |
| Game config đọc từ deploy + configVersion trong receipt | CFG-01, CFG-02 (mới) | Nếu không có thì command mẫu không tính được gì |
| GM **backend** với permission/scope/audit; GM **frontend tối thiểu** (player lookup + resource adjust) dùng auth đơn giản ở Development | SEC-02, SEC-07, SEC-14, GM-02, GM-03, GM-06 | Chứng minh security pipeline; SSO đẩy P0.5 |
| Logging file rolling + metrics cơ bản + health | LOG-01..06, OBS-01..03 | Rẻ, cần ngay để debug |
| Template module + **một module mẫu tối thiểu** + thay một policy | EXT-01..05, ACC-13 (phiên bản P0) | Không dùng Event/Leaderboard |
| Client SDK Unity tối thiểu (transport, session, requestId, retry) + IL2CPP roundtrip | CLI-01..04 (mới), ACC-18 | Để game mẫu chạy được |
| Dev/Staging config, graceful shutdown, cheat profile Dev | OPS-01, OPS-03, OPS-09 (mới) | QA cần ngay |
| Integration test trên Couchbase thật trong CI cho ACC-04..07 | mục 15.1 (mới) | Regression là tài sản của framework |

**ACC áp dụng cho P0**: ACC-01 (guest), ACC-02, ACC-04, ACC-05, ACC-06, ACC-07, ACC-09, ACC-10, ACC-12, ACC-13 (phiên bản P0), ACC-17, ACC-18.

#### Đẩy sang P0.5 (production hardening — bắt buộc trước game đầu tiên lên production)

| Hạng mục | Yêu cầu | Lý do đẩy |
| --- | --- | --- |
| Google + Facebook adapter, link, conflict, recovery | IDN-03..06, IDN-11 (mới), ACC-03 | Tích hợp ngoài, lead time ngoài team; guest đủ cho dev/QA |
| Giftcode đầy đủ (quota hot-key) | GFT-01..06, ACC-08 | Không cần để chứng minh kiến trúc; cần trước soft launch |
| GM OIDC/SSO, cookie hardening, CSRF, CORS, thu hồi quyền, Data Protection đa instance | SEC-03..06, SEC-10, SEC-11, OPS-04, ACC-11 | Phụ thuộc TBD-05; backend permission đã có ở P0 |
| GM frontend đầy đủ (giftcode CRUD, lịch sử, progression edit) | GM-01, GM-04, GM-05 | Có thể làm song song bởi frontend dev sau khi GM API ổn |
| Maintenance mode, version gate, ban/suspend, account deletion | OPS-08, API-10, GM-07, IDN-12 (mới) | Bắt buộc trước production, không cần để kiến trúc |
| Load test + capacity sign-off, restore drill, runbook, deploy/rollback | 11.1, PERF-05, OPS-02, OPS-05..07, ACC-16, ACC-19 | Phụ thuộc TBD-06/09 |
| PERF-08 cache, PERF-10 scale-out verification | PERF-08, PERF-10 | Cần ≥ 2 instance env |

P1 và P2 giữ nguyên, nhưng ACC-14, ACC-15 chuyển sang P1.

### F-D02 — Điều kiện đóng P0 phụ thuộc TBD "trước production"
- **Liên quan**: ACC-16 ↔ TBD-06, ACC-19 ↔ TBD-09, mục 16 hàng P0 "Các ACC áp dụng cho baseline đạt"
- **Mức độ**: Major
- **Đề xuất**: Đã xử lý bằng việc chuyển ACC-16/19 sang P0.5; ngoài ra TBD-06 nên có giá trị tạm lấy từ số liệu live của Sniper PVP (xem F-E04).

### F-D03 — Deliverable (mục 16) không gắn phase
- **Liên quan**: 7 deliverable ở mục 16
- **Vấn đề**: Không rõ deliverable nào phải có khi P0 đóng (ví dụ "monitoring và recovery runbook" chắc chắn chưa cần ở P0 MVP, nhưng "template module" thì cần).
- **Mức độ**: Major
- **Đề xuất**: Thêm cột Phase vào danh sách deliverable: P0 = 1, 2, 3 (SDK), 5 (schema/key/idempotency spec), 6 (integration report), PCR; P0.5 = 4, 7, performance/security report; P1 = bổ sung theo module.

---

## 5. TBD (mục 17)

### 5.1. Phân loại TBD hiện có

| TBD | Chặn gì | Phân loại | Nhận xét |
| --- | --- | --- | --- |
| TBD-01 Schema/limits/command đầu tiên | Progression/Economy model | Chặn P0 **nếu** dùng game thật; không chặn nếu dùng game mẫu | Đề xuất: P0 dùng game mẫu do backend tự định nghĩa; TBD-01 chốt trước khi game đầu tiên tích hợp |
| TBD-02 Combat evidence | Command battle-complete mẫu | Không chặn framework | Game mẫu chọn evidence = eligibility-only; TBD-02 thuộc game |
| TBD-03 Provider Google/Play Games/Facebook | IDN-03/04 | Không chặn P0 nếu Identity P0 = guest only | Chặn P0.5 |
| TBD-04 Session single/multi, takeover, deletion/unlink | IDN-08, IDN-12 | **Chặn thiết kế session P0** | Có thể chốt mặc định: multi-session, không takeover, thu hồi theo player; account deletion tách thành TBD riêng vì là compliance |
| TBD-05 SSO provider, onboarding, scope, GM origin | SEC-03..06, SEC-10 | **Chặn thiết kế GM auth** (same-origin vs CORS quyết định shape cookie/CSRF) | Với GM backend P0 chỉ cần quyết định "same-origin" có/không; SSO provider chốt P0.5 |
| TBD-06 Workload, latency, topology | 11.1, PERF-04/05, index design | Không chặn code; chặn đóng P0.5 | Lấy số tạm từ Sniper PVP live (login/day, peak RPS, payload) |
| TBD-07 Transaction boundary, durability, hot-key quota | CMD-*, ECO-04, DAT-04, GFT-04 | **Chặn thiết kế dữ liệu P0** — không thể thiết kế document shape, receipt, ledger nếu chưa biết boundary | Phải chốt đầu tiên; hot-key quota có thể tách ra P0.5 |
| TBD-08 Retention, PII | DAT-03, LOG-04, IDN-12 | Không chặn P0; cần giá trị tạm | Đặt mặc định: idempotency entry 24 h, session 30 ngày, receipt/ledger/audit không xóa |
| TBD-09 RPO/RTO, rollback/replay | OPS-07, ACC-19 | Chặn P0.5 | |
| TBD-10 Board consistency, season, nguồn score | LDB/League | Chặn P1 | |

### F-E01 — TBD-07 chặn thiết kế P0 nhưng mốc chốt là "trước triển khai mutation/redeem" — quá muộn
- **Liên quan**: TBD-07, CMD-01..07, ECO-04, DAT-04, F-A02, F-A03
- **Mức độ**: Blocker
- **Đề xuất**: Đổi mốc thành "trước khi bắt đầu thiết kế P0 (sprint 0)"; owner Backend; đầu ra là mục tương ứng trong PCR. Tách "hot-key strategy cho quota" thành TBD-07b, mốc P0.5.

### F-E02 — TBD-05 chặn thiết kế GM security pipeline
- **Liên quan**: TBD-05, SEC-04, SEC-05, SEC-06
- **Mức độ**: Major
- **Đề xuất**: Tách TBD-05 thành (a) "GM frontend same-origin với backend? (có/không)" — chốt sprint 0; (b) "SSO provider, onboarding, revocation" — chốt P0.5.

### F-E03 — TBD-04 chặn IDN-08 nhưng gộp nhiều quyết định khác bản chất
- **Liên quan**: TBD-04, IDN-08, IDN-12
- **Mức độ**: Major
- **Đề xuất**: Tách: TBD-04a session model (sprint 0, mặc định multi-session); TBD-04b link conflict UX (P0.5, product); TBD-04c account deletion/unlink (P0.5, nhưng ràng buộc bởi store policy — không phải "theo scope game").

### F-E04 — TBD-06 không có giá trị tạm dù team đang có số liệu live
- **Liên quan**: TBD-06, 11.1, PERF-04, PERF-05, ACC-16
- **Mức độ**: Major
- **Đề xuất**: Thêm cột "Giá trị tạm" vào 11.1, điền từ telemetry Sniper PVP (peak CCU, login burst sau push, RPS theo API, payload Protobuf hiện tại); ghi "giá trị tạm dùng để thiết kế, giá trị chốt dùng để sign-off".

### F-E05 — TBD-01/TBD-02 có thể tách khỏi đường găng bằng game mẫu
- **Mức độ**: Minor
- **Đề xuất**: Ghi vào TBD-01/02: "P0 dùng game mẫu `SampleGame` với 3 resource, 1 progression track, 1 command; TBD chỉ chặn tích hợp game thật."

### F-E06 — TBD còn thiếu (tài liệu ngầm phụ thuộc nhưng không liệt kê)
- **Mức độ**: Major
- **Đề xuất** thêm vào bảng 17:

| ID đề xuất | Nội dung | Owner | Mốc | Nguồn |
| --- | --- | --- | --- | --- |
| TBD-11 | Vị trí `gameId`/`environmentId`: route / header / claim / deployment config; có dùng Couchbase scope/collection theo game không | Backend | Sprint 0 | F-A06, F-B12 |
| TBD-12 | Mô hình revision: per-document CAS vs player `stateVersion`; scope của snapshot/delta | Backend + Client | Sprint 0 | F-A04 |
| TBD-13 | Replay window của idempotency entry; cơ chế tra cứu receipt (resend vs GetReceipt) | Backend + Client | Sprint 0 | F-A02, F-B17 |
| TBD-14 | Player session transport/format, access/refresh lifetime, thời gian thu hồi tối đa | Backend + Client | Sprint 0 | F-A09 |
| TBD-15 | Chủ sở hữu và phạm vi Client SDK Unity; Unity version/IL2CPP target | Client lead | Sprint 0 | F-B08 |
| TBD-16 | Resource catalog: framework hay game sở hữu; có instanced item ở P0 không | Game design + Backend | Trước Economy design | F-B02 |
| TBD-17 | Nguồn game config (deploy file / Couchbase / dịch vụ ngoài) và ai sửa | Backend + LiveOps | Trước Progression design | F-B01 |
| TBD-18 | Framework có phục vụ migrate Sniper PVP khỏi PlayFab không | Product | Sprint 0 | F-B19 |
| TBD-19 | Ngưỡng rate limit / in-flight / page size / payload theo nhóm endpoint | Backend + Ops | Trước load test | F-B23, F-C03 |
| TBD-20 | Couchbase Server version/edition và tính năng được dùng (transactions, durability level) | Backend + Ops | Sprint 0 | mục 2.2 "được kiểm tra tương thích" |
| TBD-21 | GM frontend stack, browser support, hosting | Frontend + Ops | Trước GM frontend | GM-01 |
| TBD-22 | Danh sách ACC áp dụng cho từng phase | Backend lead | Sprint 0 | F-A07 |

### F-E07 — Bảng TBD thiếu cột "Mặc định nếu chưa chốt" và "Trạng thái"
- **Mức độ**: Minor
- **Đề xuất**: Thêm hai cột; mặc định cho phép thiết kế tiếp mà không "nghiệm thu bằng giả định ngầm" (câu cuối mục 17 sẽ có cơ sở kiểm).

---

## 6. Cấu trúc tài liệu

### F-F01 — Bảng yêu cầu thiếu cột Priority, Phase, Verified by
- **Liên quan**: mục 3 (MUST/SHOULD), mục 16 (phase), mục 15 (ACC); F-A14, F-C04, F-C07
- **Mức độ**: Major
- **Đề xuất**: Chuẩn hóa mọi bảng yêu cầu thành `ID | Yêu cầu | Priority (MUST/SHOULD) | Phase (P0/P0.5/P1/P2) | Verified by`. Bỏ cột "Tiêu chí kiểm tra" riêng của ARC, gộp vào "Verified by" + ACC/Unit. Ví dụ: `SEC-03 | GM đăng nhập OIDC/SSO với MFA tại IdP | SHOULD | P0.5 | Review + ACC-11`.

### F-F02 — Gom các yêu cầu chồng chéo về một "yêu cầu gốc", nơi khác chỉ tham chiếu
- **Liên quan**: F-A01, F-A03, F-A04, F-A05
- **Mức độ**: Minor
- **Đề xuất**: Mục 8.3 đổi tên thành "Command execution contract" và là nơi duy nhất quy định idempotency/receipt/revision; mục 12 là nơi duy nhất quy định atomicity/durability; mục 13 là nơi duy nhất quy định ba loại bản ghi bền vững. GFT/EVT/GM/SEC chỉ ghi "theo CMD-xx/DAT-xx".

### F-F03 — Mục 9 trộn Giftcode (P0) với Event/Leaderboard/Job (P1)
- **Mức độ**: Minor
- **Đề xuất**: Tách Giftcode thành mục riêng ngay sau mục 8; Event/Leaderboard/Job thành mục "Module mở rộng (P1)" đặt sau mục 10.

### F-F04 — Mục 5.2 (Leaderboard → League) là thiết kế minh họa, không phải yêu cầu
- **Mức độ**: Minor
- **Đề xuất**: Chuyển sang phụ lục "Ví dụ kiểm chứng extension contract"; giữ lại trong 5.1 một câu "EXT-05 được chứng minh bằng ví dụ ở Phụ lục A ở P1".

### F-F05 — Mục 11.1 (capacity contract) trùng với TBD-06
- **Mức độ**: Minor
- **Đề xuất**: Giữ bảng 11.1 làm nơi chứa giá trị (tạm + chốt), TBD-06 chỉ trỏ tới 11.1; thêm cột "Giá trị tạm" (F-E04) và dòng rate limit (F-B23).

### F-F06 — Mục 16 (phase) đặt sau mọi yêu cầu; người đọc không biết phase khi đọc từng yêu cầu
- **Mức độ**: Minor
- **Đề xuất**: Chuyển bảng phase lên mục 2.4; chi tiết deliverable giữ ở mục 16. Kết hợp với cột Phase (F-F01).

### F-F07 — Glossary thiếu các thuật ngữ đang gây nhập nhằng
- **Liên quan**: mục 3
- **Mức độ**: Minor
- **Đề xuất** bổ sung: *Request ID* vs *Idempotency key* vs *Idempotency entry*; *Receipt* vs *Ledger entry* vs *Audit record*; *Revision (document)* vs *State version (player)*; *Snapshot* vs *Delta*; *Environment (logical)* vs *Deployment environment*; *Game scope*; *Security profile* (Player/GM/Public/Dev); *Capability* (dùng ở ECO-01, EXT-01 nhưng chưa định nghĩa); *Published Contract Register*; *Session* vs *Credential* vs *Token*; *Actor context*.

### F-F08 — Thiếu ma trận truy vết và thiếu mục "Giả định"
- **Mức độ**: Minor
- **Đề xuất**: Phụ lục B: ma trận Yêu cầu ↔ ACC ↔ Phase ↔ TBD phụ thuộc. Mục 2.4 "Giả định" liệt kê các giả định đang ngầm: một game một deployment; không migrate game live; GM và Player chung host ở P0; Couchbase self-managed; không có realtime.

---

## 7. Top 8 finding quan trọng nhất

| # | Finding | Mức độ | Vì sao đứng đầu |
| --- | --- | --- | --- |
| 1 | F-E01 TBD-07 transaction boundary chốt quá muộn | Blocker | Không thể thiết kế document/receipt/ledger khi chưa biết boundary |
| 2 | F-A02 Receipt vừa TTL vừa vĩnh viễn (GFT-05 vs CMD-07/DAT-03) | Blocker | Mâu thuẫn thật, ảnh hưởng schema P0 |
| 3 | F-D01 P0 ≈ 77% effort, không có vertical slice | Blocker | Scope không khả thi với đội đang vận hành game live |
| 4 | F-A07 P0 không có danh sách ACC; ACC-13/14 kéo P1/P2 vào P0 | Blocker | Không có cách khách quan tuyên bố P0 xong |
| 5 | F-A04 + F-A06/F-B12 Revision scope và vị trí gameId "được công bố" nhưng không ai công bố | Major | Hai quyết định định hình mọi key, route, response — không lùi được |
| 6 | F-C01 + F-C04 "Được công bố" 14 lần; 70% yêu cầu không có ACC | Major | Nghiệm thu không khách quan |
| 7 | F-B01/F-B02/F-B04/F-B06/F-B05 Config, catalog, maintenance/version gate, ban, account deletion thiếu | Major | Game live không lên được production dù P0 "đạt" |
| 8 | F-A15 + F-B08 Recovery qua provider không có yêu cầu; Client SDK không có chủ | Major | Hai chỗ Sniper PVP đã trả giá; ACC-18 không có đối tượng kiểm |

## 8. Thứ tự sửa tài liệu đề xuất

1. Chốt sprint-0 TBD: TBD-07, TBD-11..TBD-15, TBD-22 (F-E01, F-E06).
2. Thêm glossary mới (F-F07) và viết lại CMD/DAT/LOG làm "yêu cầu gốc" (F-A02, F-A03, F-A04, F-A05, F-F02).
3. Chuẩn hóa cột Priority/Phase/Verified by (F-F01), gán ID cho MUST trong đoạn văn (F-C06), thay "được công bố" bằng PCR (F-C01).
4. Tách P0/P0.5, gắn phase cho ACC và deliverable (F-D01, F-A07, F-D03).
5. Bổ sung gap theo thứ tự: CFG, ECO-07 catalog, CLI SDK, OPS-08/API-10 maintenance & version gate, GM-07 ban, IDN-11/12, OPS-09 cheat, mục 15.1 test strategy (F-B01..F-B12).
6. Ghi out-of-scope tường minh cho: IAP (hoặc P1), push, A/B, inbox, analytics, attestation, migrate PlayFab (F-B03, F-B13..F-B15, F-B18, F-B19, F-B21).
7. Phát hành v1.1 kèm ma trận truy vết (F-F08) rồi mới chuyển cho system-architect.
