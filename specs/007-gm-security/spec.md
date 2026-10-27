# Feature Specification: GM security hardening (P0.5 slice 007)

**Feature Branch**: `007-gm-security`

**Created**: 2026-10-27

**Status**: Draft

**Input**: "007 gm-security — slice đầu của P0.5. GM SSO/OIDC + cấp quyền tường minh (SEC-03), cookie chỉ mang session id
bảo vệ bằng Data Protection + ticket server-side (SEC-04), antiforgery + POST logout (SEC-05), CORS allowlist (SEC-06),
thu hồi ≤ 60 s (SEC-10), key ring dùng chung + rotation (OPS-04), quản lý staff có audit, GM frontend đầy đủ (GM-01).
Nghiệm thu ACC-10, ACC-11, ACC-49." — Owner 2026-10-27: **tạm bỏ GM frontend** (giao dev khác); slice này và các slice
P0.5 sau chỉ làm GM API + contract cho frontend.

## Scope note

- Thuộc slice này: SEC-03, SEC-04, SEC-05 (đầy đủ), SEC-06, SEC-10, OPS-04, quản lý staff qua GM API (SEC-15),
  contract GM API cho frontend (OpenAPI — deliverable #8), ACC-10, ACC-11, ACC-49.
- **GM frontend (GM-01 đầy đủ) không thuộc slice này** (owner 2026-10-27: giao dev khác; TBD-21 vẫn Mở). Trang tĩnh
  P0 (`/gm/v1/ui`) chỉ được sửa tối thiểu để vẫn chạy với CSRF (FR-020).
- Không thuộc: maintenance/version gate, ban (008); PGS/Game Center (009); giftcode GM-04 (011); secret scan CI,
  rotation secret ngoài key ring (013). Các slice đó cung cấp GM API của mình trong cùng tài liệu OpenAPI.
- TBD-05a (same-origin, `SameSite=Lax`, không CORS mặc định) và TBD-05b (thu hồi ≤ 60 s, Development local auth) đã
  chốt. TBD-21 (stack GM frontend) còn Mở, không chặn slice này vì frontend nằm ngoài phạm vi.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Staff đăng nhập GM bằng SSO công ty, chỉ khi được cấp quyền (Priority: P1)

Ở Production/Staging, staff đăng nhập bằng identity provider của công ty (MFA do IdP bắt buộc). Server chỉ cho vào khi
danh tính đó đã được cấp phép tường minh trong danh sách staff của deployment; thuộc công ty không tự có quyền. Ở
Development/Test vẫn dùng tài khoản local, đi qua đúng pipeline permission như SSO.

**Acceptance Scenarios**:

1. **Given** staff đã được cấp phép (gắn với danh tính IdP), **When** đăng nhập SSO thành công, **Then** nhận phiên GM
   với đúng permission của grant thuộc game/environment này (SEC-03, SEC-07).
2. **Given** người dùng xác thực được ở IdP nhưng chưa được cấp phép, **When** đăng nhập, **Then** bị từ chối, không tạo
   phiên, sự kiện được ghi log bảo mật (SEC-03, ACC-10).
3. **Given** staff được mời bằng email (chưa gắn danh tính), **When** lần đầu đăng nhập SSO với email đã xác minh khớp lời
   mời, **Then** danh tính IdP được gắn vĩnh viễn vào staff đó; các lần sau chỉ khớp theo danh tính IdP, không theo email.
4. **Given** deployment yêu cầu bằng chứng MFA, **When** IdP trả phiên không có MFA, **Then** từ chối đăng nhập.
5. **Given** môi trường Production, **When** gọi đăng nhập local, **Then** endpoint không tồn tại (404); cấu hình tài khoản
   local ở Production làm khởi động thất bại.

---

### User Story 2 - Phiên GM chống đánh cắp và chống giả mạo yêu cầu (Priority: P1)

Cookie GM chỉ chứa định danh phiên đã được bảo vệ mật mã; mọi dữ liệu phiên nằm ở server. Mọi thao tác thay đổi dữ
liệu bằng cookie phải kèm bằng chứng chống CSRF và không được đến từ site khác. Đăng xuất chỉ bằng POST có bằng chứng
CSRF và huỷ phiên ở server.

**Acceptance Scenarios**:

1. **Given** phiên GM hợp lệ, **When** gửi mutation thiếu hoặc sai CSRF token, **Then** 403, không thay đổi dữ liệu (ACC-11).
2. **Given** mutation có `Sec-Fetch-Site: cross-site`, **When** gửi kèm token hợp lệ, **Then** vẫn 403 (SEC-05).
3. **Given** phiên GM, **When** gọi `GET` vào endpoint đăng xuất, **Then** bị từ chối (405), phiên còn nguyên (ACC-11).
4. **Given** đăng xuất bằng POST hợp lệ, **When** dùng lại đúng cookie cũ (kể cả đã chép trước đó), **Then** 401 trên mọi
   instance — xoá cookie phía client không phải là cơ chế thu hồi (SEC-10).
5. **Given** cookie bị sửa một byte hoặc tự tạo, **When** gửi, **Then** 401.
6. **Given** request mang cookie từ origin không có trong allowlist, **When** gọi API GM, **Then** không có header CORS cho
   origin đó và trình duyệt không đọc được response; mutation bị từ chối (ACC-11).

---

### User Story 3 - Thu hồi quyền và phiên có hiệu lực nhanh trên mọi instance (Priority: P1)

Quản trị viên khoá staff, đổi quyền hoặc thu hồi mọi phiên của một staff; thay đổi có hiệu lực trên mọi instance trong
tối đa 60 giây.

**Acceptance Scenarios**:

1. **Given** staff đang có phiên trên instance A và B, **When** admin khoá staff, **Then** trong ≤ 60 s mọi request của
   staff đó trên cả A và B trả 401 (ACC-11, SEC-10).
2. **Given** staff có `resource.adjust`, **When** admin bỏ permission này, **Then** trong ≤ 60 s thao tác adjust trả 403 trên
   mọi instance, các thao tác còn quyền vẫn chạy (không buộc đăng nhập lại).
3. **Given** admin chọn "thu hồi mọi phiên" của staff, **When** staff dùng bất kỳ phiên nào đã cấp trước đó, **Then** 401 trên
   mọi instance trong ≤ 60 s; staff đăng nhập lại được nếu vẫn còn quyền.
4. **Given** phiên quá thời hạn tuyệt đối hoặc không hoạt động quá thời hạn idle, **When** dùng, **Then** 401.

---

### User Story 4 - Nhiều instance dùng chung khoá bảo vệ, xoay khoá không làm rớt phiên (Priority: P1)

Cookie do instance A phát hành dùng được ở instance B. Khoá bảo vệ được xoay theo lịch; cookie phát hành bằng khoá cũ
vẫn dùng được trong thời gian grace. Khoá lưu ở kho dùng chung và được mã hoá khi lưu.

**Acceptance Scenarios**:

1. **Given** hai instance cùng deployment, **When** đăng nhập ở A rồi gọi ở B bằng cùng cookie, **Then** hợp lệ (ACC-49).
2. **Given** khoá mới được kích hoạt (rotate), **When** dùng cookie phát hành trước khi rotate, **Then** vẫn hợp lệ trong
   suốt thời gian grace; cookie mới dùng khoá mới (ACC-49).
3. **Given** kho khoá dùng chung, **When** đọc tài liệu khoá trực tiếp từ kho, **Then** không thấy khoá ở dạng rõ (OPS-04).
4. **Given** môi trường ngoài Development/Test thiếu khoá chính để mã hoá key ring, **When** khởi động, **Then** thất bại
   với thông báo rõ ràng.

---

### User Story 5 - Quản lý staff có audit, chống leo thang quyền (Priority: P2)

Người có quyền quản lý staff tạo staff (mời bằng email hoặc gắn danh tính IdP), gán role/permission theo phạm vi
game/environment, khoá/mở khoá, thu hồi phiên; mọi thay đổi có audit.

**Acceptance Scenarios**:

1. **Given** quyền `staff.manage`, **When** tạo staff với role Support, **Then** staff có grant đã expand cho game/env
   này; có một bản audit ghi actor, hành động, staff đích, before/after, lý do, thời gian, correlation (SEC-14, SEC-15).
2. **Given** người quản lý không có `resource.adjust`, **When** cố cấp `resource.adjust` cho người khác, **Then** 403 (không
   cấp được quyền mình không có).
3. **Given** chỉ còn một Administrator đang hoạt động, **When** khoá hoặc hạ quyền người đó (kể cả tự mình), **Then** bị từ
   chối, deployment không bị khoá ngoài.
4. **Given** chỉ có `staff.read`, **When** xem danh sách staff, **Then** được (có phân trang); **When** sửa, **Then** 403.
5. **Given** permission không thuộc catalog của deployment, **When** gán, **Then** 400.

---

### User Story 6 - Dev frontend có contract GM API đầy đủ (Priority: P3)

Dev làm GM frontend (ngoài slice này) có tài liệu mô tả mọi endpoint GM (request, response, lỗi, permission, CSRF) và
một endpoint cho biết staff hiện tại có những permission nào để ẩn/hiện chức năng.

**Acceptance Scenarios**:

1. **Given** deployment ở Development, **When** tải tài liệu GM API, **Then** mọi endpoint GM đã đăng ký đều có mặt kèm
   permission yêu cầu và yêu cầu CSRF; endpoint thiếu trong tài liệu làm test thất bại.
2. **Given** staff đăng nhập, **When** gọi `me`, **Then** nhận staffId, tên, game/env và danh sách permission hiệu lực.

### Edge Cases

- Callback SSO có `state`/`nonce` sai, hết hạn hoặc bị dùng lại → từ chối, không tạo phiên.
- Hai lần đăng nhập SSO đồng thời cho cùng lời mời email → danh tính chỉ được gắn một lần.
- IdP thu hồi tài khoản: phiên GM hiện có vẫn sống tới khi staff bị khoá trong GM hoặc phiên hết hạn (GM không nhận tín
  hiệu từ IdP); runbook ghi bước khoá staff.
- Kho khoá tạm thời không đọc được khi khởi động → khởi động thất bại (không tự sinh key ring riêng trên instance).
- Allowlist CORS có `*`, có origin `http://` ở Production, hoặc chứa path → khởi động thất bại.
- Staff tự thu hồi phiên của chính mình → phiên hiện tại cũng bị thu hồi.
- Lời mời email trùng với staff khác → 409.

## Requirements *(mandatory)*

### Functional Requirements

**Đăng nhập (SEC-03)**

- **FR-001**: Đăng nhập GM ngoài Development/Test chỉ qua OpenID Connect (authorization code + PKCE) tới IdP cấu hình
  theo environment; client secret đọc từ secret provider (OPS-08), thiếu → khởi động thất bại.
- **FR-002**: Chỉ staff có bản ghi trong `gm_staff` của deployment, không bị khoá, có danh tính IdP khớp
  (issuer + subject) mới được tạo phiên. Danh tính IdP chưa khớp → từ chối, ghi log bảo mật (không log token).
- **FR-003**: Lời mời bằng email: staff tạo trước với email; lần đăng nhập SSO đầu tiên có email **đã xác minh** khớp
  (không phân biệt hoa thường) gắn issuer + subject vào staff, nguyên tử, một lần; sau đó chỉ khớp theo issuer + subject.
- **FR-004**: Tuỳ chọn yêu cầu bằng chứng MFA trong phiên IdP (mặc định bật ngoài Development/Test, tắt được có ghi nhận
  trong PCR khi IdP ép MFA bằng chính sách mà không phát bằng chứng).
- **FR-005**: Bootstrap Administrator đầu tiên ở môi trường không có local login: danh sách danh tính/email cấu hình theo
  environment, áp dụng idempotent khi khởi động, chỉ khi deployment chưa có Administrator đang hoạt động; có audit.
- **FR-006**: Development/Test giữ đăng nhập local (FR-004 slice 004) và cùng tạo phiên, cùng pipeline permission, cùng
  CSRF như SSO; endpoint local không tồn tại ở môi trường khác.

**Phiên (SEC-04, SEC-10)**

- **FR-007**: Cookie `gs_gm` chỉ chứa session id ngẫu nhiên 256-bit đã được bảo vệ (mã hoá + xác thực) bằng key ring dùng
  chung; ticket (staff, thời điểm tạo, hết hạn, epoch) ở server trong `gm_sessions`, lưu theo hash. Cookie `HttpOnly`,
  `Secure` (ngoài Development), `SameSite=Lax`, `Path=/gm`; `SameSite=None` chỉ khi bật CORS (FR-013).
- **FR-008**: Hết hạn tuyệt đối (mặc định 8 h) và idle (mặc định 60 phút, gia hạn lười); giá trị trong PCR.
- **FR-009**: Staff có `sessionEpoch`; phiên mang epoch lúc tạo; epoch tăng khi khoá staff hoặc "thu hồi mọi phiên" → mọi
  phiên cũ vô hiệu. Đổi permission có hiệu lực mà không cần đăng nhập lại.
- **FR-010**: Mọi cache phiên/staff/permission ≤ 60 s (SEC-10, PERF-08); hiệu lực thu hồi ≤ 60 s trên mọi instance.

**CSRF, logout, CORS (SEC-05, SEC-06)**

- **FR-011**: Mọi request GM thay đổi dữ liệu (method ngoài GET/HEAD/OPTIONS), kể cả đăng nhập local và đăng xuất, phải có
  CSRF token hợp lệ ở header `X-CSRF-Token` và `Sec-Fetch-Site` ≠ `cross-site`; thiếu/sai → 403 `CSRF_FAILED`
  (Problem Details). Token lấy từ endpoint GET same-origin.
- **FR-012**: Đăng xuất chỉ `POST /gm/v1/auth/logout` (FR-011); xoá ticket server-side và cookie; `GET` → 405.
- **FR-013**: CORS tắt mặc định (same-origin, TBD-05a). Khi cấu hình allowlist origin theo environment: chỉ origin khớp
  chính xác (scheme + host + port) nhận header CORS có credentials; không wildcard; Production chỉ `https`; cấu hình sai
  → khởi động thất bại. CORS không thay thế xác thực/CSRF.

**Key ring (OPS-04)**

- **FR-014**: Mọi instance của deployment dùng chung key ring lưu trong Couchbase (collection riêng, phạm vi game), mỗi
  khoá được mã hoá khi lưu bằng khoá chính từ secret provider; thiếu khoá chính ngoài Development/Test → khởi động thất
  bại.
- **FR-015**: Khoá có thời hạn kích hoạt (mặc định 90 ngày) và tự xoay; khoá cũ vẫn giải mã được trong thời gian grace
  (≥ hết hạn tuyệt đối của phiên); thao tác "rotate ngay" cho ops; giá trị trong PCR.

**Quản lý staff (SEC-15, SEC-14)**

- **FR-016**: API + trang staff: liệt kê (phân trang, GM-06), xem, tạo (email mời hoặc issuer + subject), gán role/permission
  theo scope game/environment, khoá/mở khoá, thu hồi mọi phiên. Permission `staff.read`, `staff.manage`.
- **FR-017**: Chống leo thang: chỉ cấp permission mà người thao tác đang có; không xoá/khoá/hạ quyền Administrator đang
  hoạt động cuối cùng; permission ngoài catalog → 400.
- **FR-018**: Mỗi thay đổi staff ghi đúng một bản audit (actor, permission/action, staff đích, lý do, before/after, thời
  gian UTC, correlation), nguyên tử với thay đổi; xem được trong trang staff (phân trang).
- **FR-019**: Sự kiện đăng nhập thành công/thất bại, từ chối CSRF, đăng xuất được ghi log bảo mật có cấu trúc (không có
  token, cookie, secret).

**Contract cho GM frontend (deliverable #8; frontend ngoài phạm vi)**

- **FR-020**: Tài liệu OpenAPI của GM API sinh từ endpoint đã đăng ký (gồm permission và yêu cầu CSRF của từng endpoint),
  phục vụ ở Development/Test và commit vào repo; test bảo đảm mọi endpoint GM có trong tài liệu. Trang tĩnh P0
  (`/gm/v1/ui`) sửa tối thiểu để lấy và gửi CSRF token; không phát triển thêm UI.
- **FR-021**: `GET /gm/v1/me` trả staffId, tên hiển thị, game/environment và permission hiệu lực (để frontend ẩn/hiện);
  backend vẫn quyết định mọi quyền.

### Key Entities *(include if feature involves data)*

- **Staff**: staffId, tên hiển thị, email (lời mời), danh tính IdP (issuer + subject), trạng thái khoá, `sessionEpoch`,
  grant theo (game, environment) → permission; password hash chỉ ở Development/Test.
- **GM session ticket**: hash session id → staff, epoch, thời điểm tạo, lần hoạt động cuối, hết hạn tuyệt đối/idle,
  game/env.
- **Data Protection key**: id, ngày tạo/kích hoạt/hết hạn, nội dung đã mã hoá bằng khoá chính.
- **Staff audit record**: actor, action, permission, staff đích, lý do, before/after, thời gian, correlation.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% mutation GM trong bộ test bị từ chối khi thiếu/sai CSRF token hoặc đến từ site khác.
- **SC-002**: Thu hồi (khoá staff, bỏ permission, thu hồi phiên) có hiệu lực trên 2 instance trong ≤ 60 s ở 100% lần chạy.
- **SC-003**: Cookie phát hành ở một instance dùng được ở instance khác, và trước/sau một lần rotate khoá, trong 100% lần chạy.
- **SC-004**: 0 người dùng chưa được cấp phép tường minh tạo được phiên GM.
- **SC-005**: Mỗi thay đổi staff có đúng 1 bản audit đủ 7 trường.
- **SC-006**: 100% endpoint GM đã đăng ký có mặt trong tài liệu OpenAPI.

## Assumptions

- IdP là IdP chuẩn OpenID Connect (vd. Microsoft Entra ID, Google Workspace, Keycloak); slice này không chọn IdP cụ thể
  (TBD-05b chỉ chốt thời gian thu hồi). Test tự động dùng IdP giả lập; đăng nhập với IdP thật là kiểm thủ công.
- Khoá chính bảo vệ key ring là secret 256-bit từ secret provider (OPS-08); xoay khoá chính là việc của slice 013.
- Thu hồi ≤ 60 s dựa trên TTL cache, không cần kênh push giữa các instance.
- Audit staff không gắn với player nên lưu cùng kho audit GM nhưng target là staff.
- GM frontend do dev khác làm dựa trên tài liệu OpenAPI (FR-020); các slice P0.5 sau chỉ giao GM API.
