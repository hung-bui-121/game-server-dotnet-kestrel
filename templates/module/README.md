# Module template

Khung 2 project của một module (mục 4.2 tài liệu yêu cầu). Template được build cùng `GameServer.slnx` để
luôn khớp API hiện hành.

1. Copy `Template.Module` và `Template.Module.Adapters` vào `modules/<YourModule>/`; sửa đường dẫn
   `ProjectReference` (`../../../src/...` vẫn đúng khi đặt ở `modules/<YourModule>/`).
2. Đổi tên namespace, `ModuleName`, `AggregateId`, `Operation`, dải error code (không chồng dải module khác),
   `package`/`csharp_namespace` trong `.proto`.
3. Đăng ký ở host: `gameServer.AddModule<YourModule>()`.
4. Viết PCR `docs/pcr/<your-module>.md` (aggregate, command → Touches/Reads, error codes, capability thay được).

Chi tiết: `docs/guides/module-authoring.md`.
