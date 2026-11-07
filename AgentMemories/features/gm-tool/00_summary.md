# RESUME: GM tool (GM console + GM API) — framework shell, game mở rộng

**Trạng thái (2026-11-07):** GM-1 (slice 017 `gm-api-contract`) XONG (`e182fba`); GM-2 (slice 018
`gm-console-hosting-shell`) XONG trên nhánh `018-gm-console-hosting-shell` (70/70 task, review r2 YES). Bước kế tiếp: **GM-3
`gm-generic-forms`**.
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

## Kết quả GM-1 (slice 017, 2026-11-05)

- VERIFY: V1 sai một phần (generator chỉ đọc `[property:]`, bỏ `[Required]`, `required` = mọi tham số positional) → fallback
  đã thiết kế: transformer ghi keyword từ cùng walk với server. V10 đúng (route Public của module allowlist qua declaration,
  test vĩnh viễn `PublicDeclarationAllowlistTests`). V11 đúng (`IOpenApiDocumentProvider` keyed `"gm"`, không request, không
  `servers`). V12: enum mô tả bằng schema transformer (chuỗi camelCase của `GameServerJson.Options`); review r1 bỏ enum converter toàn host. V13 đúng (`AssociatedParameter.AttributeProvider`).
- Route mới: `gm.console_catalog`, `gm.auth_methods`, `economy.resource_options`, `identity.ban_scopes`; `/me` có
  `frameworkVersion`/`gameVersion`; kit `GmOpenApiSnapshot`; fixture có GM command `fixture.note_set` + option route.
- Đo SC-004: catalog lần đầu 195 ms, warm p95 0,32 ms.
- Quyết định chờ owner (D/A + review): danh sách 1–9 ở `specs/017-gm-api-contract/release-notes-draft.md`.
- Ghi chú cho GM-2: CORS cần expose `ETag` cho catalog; catalog ghi đè `no-store` thành `private, no-cache`; 415 cho
  Content-Type sai trên mọi GM mutation có `Accepts`.

## Kết quả GM-2 (slice 018, 2026-11-07)

- Phase A (backend, `63ddb8f`): cookie `__Host-gs_gm` / `__Host-gs_gm_csrf` ngoài Development, `gs_gm_dev` / `gs_gm_dev_csrf` ở
  Development (`Path=/`); `SameSite` từ `Gm:Cookies:SameSite` (Lax; `None` chỉ Dev/Test); CORS expose `X-Request-Id`,
  `Retry-After`, `ETag`, preflight 7200 s, một origin ngoài Development; SSO về `Gm:Console:BaseUrl` + `/` hoặc `/#…`, từ chối về
  `BaseUrl#/login?error=sso_denied`; `/gm/v1/ui` chỉ Dev/Test. Mọi staff đăng nhập lại một lần (BC-1).
- Phase B: workspace `gm-console/` (shell `@gameserver/gm-shell` dạng source, app mặc định, `deploy/` một nguồn header, e2e trên
  Chrome/Edge của máy), trang đọc Players / Player (+ Receipts, Audit, Ledger) / Receipt / Staff / Ops, guard Q19, cảnh báo lệch
  version, ranh giới mutation ba lớp (lint, quét source, guard `MUTATION_NOT_ALLOWED` lúc chạy). Docs: PCR gm + platform §24,
  `docs/guides/gm-console.md`, consumer guide §15.
- VERIFY: V2 đúng (header CORS trên 12 status lỗi, H14 không tái hiện, `ErrorResponseWriter` không đổi); V3 `style-src 'self'` giữ
  được sau khi bỏ `<style>` của Mantine (CSS variables, `SimpleGrid`); V4 đúng (cookie `Lax` trên fetch cùng site, Chrome 157, Edge
  156); V5 đúng (`__Host-` + antiforgery trên HTTPS, `Always` lỗi trên HTTP → tên Development); V7 không chạy (không có Firefox);
  V8 version trong bảng T025, npm 10.9.3 cần `overrides` vitest; V9 đúng (workspace game hoist một React; phải dùng cả thư mục
  `framework/gm-console`, `.npmrc` chặn scope `@gameserver` ở root game); V11 đúng (preview + plugin header); V12 đúng (codegen xác
  định, LF); V13 đúng (catalog revalidate `If-None-Match` → 304).
- Suite: in-memory 3026 (0 lỗi); File 1 lỗi flaky `TelemetryTests` (chạy lại xanh); Couchbase 32 lỗi timeout provisioning fixture
  (chạy lại project 151, 0 lỗi); `npm run verify` 231 test; e2e Chrome + Edge xanh. Review T068 r1 CONDITIONAL (2 Major, 8 minor)
  → T069; r2 YES (5 minor + sửa spec FR-052/FR-054, đã xong).
- Chuyển sang GM-3: `x-gm-kind` thiếu/lạ trên operation không phải GET phải coi là `command`; guard `MUTATION_NOT_ALLOWED` thay bằng
  kiểm theo catalog của form chung.
- Chờ owner (danh sách ở `specs/018-gm-console-hosting-shell/release-notes-draft.md`): D4 tên cookie Development; D2/BC-5 một
  origin console mỗi env ngoài Development; D3 `SameSite=None` bị từ chối ngoài Dev/Test; D10 không mutation nghiệp vụ đến GM-3;
  D14 e2e với host chạy ngoài test; dòng GM-01 trong file requirements (chưa sửa: "P0 … resource adjust" ở frontend); thay đổi
  FR-054 (đăng nhập không gọi `/me` thêm, token phiên lấy lười — spec đã sửa); 017 D9 (version trong `/me`) và D15 (không bump/tag,
  tag kế đề xuất v0.8.0) vẫn chờ.

## Bước kế tiếp

1. 017 + 018 XONG trên nhánh. Chờ owner: merge main, push, xác nhận "Decisions awaiting owner override" của 017 và 018.
2. GM-3 `gm-generic-forms`: form command chung sinh từ catalog (xác nhận, gõ lại, `reason`, request id, không tự retry), các thao
   tác staff/gate/key/resource/ban và command của game; thay guard GM-2 bằng kiểm catalog.
