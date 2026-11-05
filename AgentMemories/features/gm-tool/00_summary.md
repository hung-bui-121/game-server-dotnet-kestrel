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

## Câu hỏi mở mới (mặc định trong ngoặc; không chặn GM-1) — chi tiết `01_design.md` §17

Q16 bố trí domain (trường hợp A, `gm.*` → `gm-api.*`, custom domain + TLS) · Q17 `/gm/v1/ui` (chỉ Dev/Test từ GM-2, xoá
sau) · Q18 console cho game không trang riêng (CI build từ source submodule) · Q19 version skew (chặn chỉ sai game/env;
cảnh báo framework minor) · Q20 cookie `__Host-` + `Path=/` (có) · Q21 GM-2 một slice hai phase (có).

## Bước kế tiếp

1. Mở slice GM-1 theo quy trình Spec Kit (`p05-speckit/00_summary.md` "How to resume" bước 3), đầu vào = `01_design.md`
   §19; VERIFY V1, V10 trong slice.
2. Trước GM-2: owner trả lời Q16–Q21 (hoặc nhận mặc định).
