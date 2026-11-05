# RESUME: GM tool (GM console + GM API) — framework shell, game mở rộng

**Trạng thái (2026-11-05):** THIẾT KẾ BẢN SỬA 1 theo quyết định owner 2026-11-05. Bước kế tiếp: **slice GM-1
`gm-api-contract`** bằng Spec Kit (phạm vi chốt ở `01_design.md` §19). Chưa có code, chưa commit.
**Thiết kế:** `01_design.md` (đọc §0, rồi §6 hosting/bảo mật, §19 phạm vi GM-1).

**Bối cảnh:** owner tự làm GM frontend (các dòng "giao dev khác" trong `docs/pcr/gm.md`, `p05-speckit/00_summary.md`,
auto-memory `gm-frontend-out-of-scope.md` đã lỗi thời — sửa trong GM-1). Chiến lược: framework ship backend base + frontend
shell + extension slot; form chung sinh từ GM OpenAPI; chỉ thứ đã chứng minh là chung mới vào framework.

## Quyết định owner 2026-11-05

- TBD-21 stack: **React + TypeScript + Vite + Mantine**.
- Trình duyệt: Chrome/Edge desktop; Firefox best-effort.
- Hosting: **console trên static host riêng (CDN/web server)** — mở lại giả định 007 (same-origin, Lax, không CORS).
- Q5–Q15: chấp nhận mặc định của architect (Q4 không còn ý nghĩa).

## Thiết kế sau sửa (tóm tắt)

- Catalog = OpenAPI của host + `x-gm-*`, mọi env cho `gm.access` (`/gm/v1/console/openapi.json`); nav = `/me` × catalog.
- GM-1 sửa contract: body/path/header cho GM command, `operationId` = route name, DataAnnotations do server kiểm, nguồn lựa
  chọn động, list phân trang có type, `gm.auth_methods`, version trong `/me`, TestKit `GmOpenApiSnapshot`.
- Hosting khuyến nghị **trường hợp A**: console và GM API khác origin nhưng **cùng site** (`gm.<env>.<domain>` →
  `gm-api.<env>.<domain>`) → cookie giữ `SameSite=Lax`, CORS exact origin + credentials theo env. Khác site (domain CDN mặc
  định) không hỗ trợ (vỡ Firefox).
- GM-2 backend: `SameSite` tách khỏi CORS, cookie `__Host-`, SSO quay về `Gm:Console:BaseUrl` (sửa 404 `/gm/`), header CORS
  trên response lỗi (`ErrorResponseWriter` đang xoá), expose headers, `/gm/v1/ui` chỉ Dev/Test.
- Frontend: shell dạng source trong `framework/gm-console/` (npm workspace), app mặc định, **không commit bundle**; game không
  trang riêng build app mặc định trong CI + `config.json` theo env; game có trang riêng dùng workspace npm ở root game.
  Codegen `openapi-typescript` + `openapi-fetch`, CI diff + `tsc`. Guard: chặn khi `/me` khác game/env của config; version
  lệch chỉ cảnh báo.

## Slice

GM-1 `gm-api-contract` (bắt đầu ngay) → GM-2 `gm-console-hosting-shell` → GM-3 `gm-generic-forms` → GM-4
`gm-console-extension`. Giftcode/mail/event: trang đi cùng slice module.

## Q16–Q21 — owner trả lời 2026-11-05 — chi tiết `01_design.md` §17

- **Q16: chưa có tên miền** → GM-2 làm và nghiệm thu bằng **local testing trước** (Vite trên `localhost` gọi `run-local`,
  cross-origin thật, không proxy). Trường hợp A (`gm.*` → `gm-api.*`, custom domain + TLS) vẫn là hướng khi có domain;
  cấu hình phải đổi được theo env mà không sửa code. Lưu ý GM-2: `localhost` khác port = cùng site; cookie `__Host-` cần
  `Secure` — kiểm trình duyệt chấp nhận trên `http://localhost`.
- Q17–Q21: **nhận mặc định** — Q17 `/gm/v1/ui` chỉ Dev/Test từ GM-2, xoá sau · Q18 CI game build app mặc định từ source
  submodule · Q19 chặn chỉ khi sai game/env, cảnh báo framework minor · Q20 cookie `__Host-` + `Path=/` · Q21 GM-2 một
  slice hai phase (backend trước).
- Còn chờ owner xác nhận (đang làm theo mặc định): 017 D9 (version trong `/me` cho mọi `gm.access`, rộng hơn 013
  `ops.read`), D15 (không bump/tag trong slice, tag kế đề xuất v0.8.0).

## Bước kế tiếp

1. Slice GM-1 = **017 `gm-api-contract`** (nhánh `017-gm-api-contract`, spec `e5851ec`) — đang implement.
2. Sau 017: GM-2 `gm-console-hosting-shell` theo Q16–Q21 ở trên.
