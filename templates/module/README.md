# Module template

> Đúng với framework v0.7.0 (chưa phát hành).

Khung 2 project của một module (mục 4.2 tài liệu yêu cầu). Template được build cùng `GameServer.slnx` để
luôn khớp API hiện hành.

1. Copy `Template.Module` và `Template.Module.Adapters`, rồi sửa đường dẫn `ProjectReference` tới framework:
   - **trong repo framework** (`modules/<YourModule>/`): `../../../src/...` của template vẫn đúng;
   - **trong repo game** (framework là submodule `framework/`, `GameServerRoot` khai trong `Directory.Build.props` của game):
     `$(GameServerRoot)src/GameServer.Platform.Web/GameServer.Platform.Web.csproj` (adapter) và
     `$(GameServerRoot)src/GameServer.Core/GameServer.Core.csproj` (nếu project nghiệp vụ tham chiếu Core trực tiếp).
     Xem `docs/guides/consumer-guide.md` mục 3.
2. Đổi tên namespace, `ModuleName`, `AggregateId`, `Operation`, `package`/`csharp_namespace` trong `.proto`, và dải error
   code: template dùng 90000–90099 (framework giữ chỗ); **module của game chọn dải trong 50000–89999**
   (`ErrorCodeBands.GameStart`–`GameEnd`), không chồng dải module khác.
3. Route: `TemplateRoutes.Earn` (`RouteDeclaration.Command(...)`, trong project nghiệp vụ) nằm trong
   `ModuleDescriptor.Routes`; adapter map từ khai báo (`MapPlayerCommand(TemplateRoutes.Earn, …)`). Đổi tên route theo luật
   `<area>.<route>` chữ thường; endpoint không khai → startup fail.
4. Wire mapper kèm kiểu message: `AddAggregateWireMapper<TemplateState, Wire.TemplateState>(…)`; member server giữ lại có
   chủ ý khai bằng `o => o.Hidden("…")`. Thêm test `WireConformance.CheckModules([new YourModule()]).ThrowIfFailed()`
   (`docs/guides/module-authoring.md` mục 8).
5. Đăng ký ở host: `gameServer.AddModule<YourModule>()`.
6. Sinh code Unity cho route, section và `.proto` của module bằng `tools/GameServer.WireGen` (`docs/guides/module-authoring.md`
   mục 9; repo game: `docs/guides/consumer-guide.md` mục 10).
7. Viết PCR `docs/pcr/<your-module>.md` (aggregate, command → Touches/Reads, error codes, capability thay được, route và
   section).

Chi tiết: `docs/guides/module-authoring.md`; repo game: `docs/guides/consumer-guide.md`.
