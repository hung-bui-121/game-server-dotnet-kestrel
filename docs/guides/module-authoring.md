# Viết module cho Game Server Framework

> Đúng với framework v0.7.0 (chưa phát hành).

Hướng dẫn này dùng `modules/Sample.Counter` làm ví dụ đầy đủ. Template rỗng ở `templates/module/`. Module viết trong repo
game (framework là submodule): xem thêm [consumer-guide.md](consumer-guide.md).

## 1. Hai project

| Project | Chứa | Tham chiếu |
| --- | --- | --- |
| `MyModule` | state aggregate, command/result (domain), handler, policy, error codes, `ModuleDescriptor` | chỉ `GameServer.Core` |
| `MyModule.Adapters` | `IGameModule` + `IPlayerApiModule`/`IGmApiModule`, `.proto`, mapping wire ↔ domain, wire mapper, service implementation, startup check (`IStartupCheck`) | module + `GameServer.Platform.Web` |

Arch test chặn project nghiệp vụ tham chiếu HTTP, Couchbase, Protobuf hoặc `GameServer.Platform` (ARC-01). Vì vậy startup
check nằm ở project `.Adapters`: `IStartupCheck`, `StartupCheckResult` và `GameConfigStatus` ở `GameServer.Platform`. Khai báo
file, luật và catalog (`ConfigFiles`, `ConfigCatalogs`, `ConfigJson`) ở `GameServer.Core`, nên nằm ở project nghiệp vụ.

## 2. Aggregate

```csharp
public sealed record MyState { public long Points { get; init; } }

public static PlayerAggregateDescriptor<MyState> Descriptor { get; } =
    new("mything", schemaVersion: 1, clientVisible: true, createDefault: () => new MyState());
```

- Id `[a-z][a-z0-9_]{1,31}`, lưu ở collection `pa_mything`, key = `playerId`.
- Tăng `schemaVersion` thì thêm một `IAggregateUpgrader` cho mỗi bước (`DelegateAggregateUpgrader`).
- Client-visible thì adapter phải đăng ký wire mapper **kèm kiểu message** (mục 5.2):
  `services.AddAggregateWireMapper<MyState, Wire.MyState>("mything", s => new Wire.MyState { ... })`.

## 3. Command

```csharp
public static CommandDescriptor Descriptor { get; } = new("mything.earn", touches: ["mything"], reads: ["profile"]);

public sealed class EarnHandler(IMyPolicy policy) : ICommandHandler<EarnCommand, EarnResult>
{
    public ValueTask<CommandOutcome<EarnResult>> HandleAsync(EarnCommand request, ICommandContext ctx)
    {
        var state = ctx.Get<MyState>("mything");
        if (!policy.Allows(request)) return ValueTask.FromResult(CommandOutcome<EarnResult>.Reject(MyErrors.NotAllowed));
        ctx.Set("mything", state with { Points = state.Points + request.Amount });
        ctx.AddOutbox("mything_log", new { request.Amount });
        return ValueTask.FromResult(CommandOutcome<EarnResult>.Ok(new EarnResult(state.Points + request.Amount)));
    }
}
```

Luật của handler: chỉ dùng `ICommandContext`; không gọi HTTP/DB; có thể chạy lại nhiều lần; chỉ `Set` aggregate
trong `Touches`; dùng `ctx.Now` (không `DateTime.UtcNow`, `DateTime.Today`, `TimeZoneInfo.Local` — analyzer chặn); hiệu
ứng "một lần" dùng `ctx.TryClaimAsync(collection, key)` (collection đó phải khai trong `Collections` kèm `Deletion`, xem
mục 4). Reject → không ghi gì, không receipt. `CommandOutcome.Commit(result, code)` commit và trả `code`: `code` phải có
trong catalog (mã core hoặc `ErrorCodeSet` của một module đã `AddModule`, đúng instance đã khai), HTTP 200; nếu không,
command trả `INTERNAL` và không commit gì (receipt lưu code theo tên). Mã HTTP ≠ 200 thì dùng `Reject`.
Result (đã encode) lớn hơn `GameServer:Commands:ResultCompactMaxBytes` hoặc receipt lớn hơn
`GameServer:Commands:ReceiptMaxBytes` → runner từ chối bằng `COMMAND_OUTPUT_TOO_LARGE` (HTTP 200, `details` `part`/`bytes`/`limit`),
không commit gì (slice 016, FG-016; giới hạn và chi phí: PCR platform §22). Kiểm trong unit test bằng
`CheckCommit(outcome, encodeResult)` (mục 8).

## 4. Descriptor và đăng ký

```csharp
public static class MyErrors
{
    // Khai ErrorCode TRƯỚC ErrorCodeSet liệt kê nó: static field khởi tạo theo thứ tự khai báo.
    // Ví dụ dùng dải của template (90000–90099). Module của game chọn dải trong 50000–89999 (ErrorCodeBands).
    public static readonly ErrorCode NotAllowed = new(90001, "MYTHING_NOT_ALLOWED", 200, false, "Not allowed.");

    public static ErrorCodeSet All { get; } = new(90000, 90099, NotAllowed);
}

public ModuleDescriptor Describe() => new()
{
    Name = "my.module",
    DependsOn = ["identity"],
    Permissions = ["mything.read"],
    Aggregates = [Descriptor],
    Commands = [Command.Define<EarnCommand, EarnResult>(EarnHandler.Descriptor).HandledBy<EarnHandler>()],
    Routes = [MyRoutes.Earn],                       // mục 5
    ErrorCodes = MyErrors.All,
    OutboxProjections = [new OutboxProjection("mything_log", "mything_log")],
    // Log giữ lịch sử, player id được ẩn danh khi xoá tài khoản (PRIV-01, FR-019).
    Collections = [new CollectionSpec("mything_log", Deletion: DeletionTreatment.Anonymize)],
};
```

- Dải error code không được chồng nhau:

  | Dải | Chủ |
  | --- | --- |
  | 0–999 | core |
  | 10000–10099 | `sample.counter` |
  | 11000–11099 | `economy` |
  | 12000–12099 | `iap` |
  | 13000–13099 | `gm` |
  | 14000–14099 | `identity` |
  | 40000–40099 | `sample.game` |
  | **50000–89999** | **module của game** (`ErrorCodeBands.GameStart`–`GameEnd`); mỗi module game chọn một dải con |
  | 90000–90099 | template (`template.module`), ví dụ trong tài liệu |
  | 90000–99999 | framework giữ chỗ (`ErrorCodeBands.FrameworkReservedStart`–`FrameworkReservedEnd`) |

  Module của framework (core, built-in, sample, template) không bao giờ dùng dải game: `ErrorCodeBandTests` fail, nêu tên
  module và dải. Chồng dải, trùng tên/số → startup fail. `ErrorCodeSet` có phần tử null (khai `ErrorCode` dưới set) →
  `AddModule` ném `TypeInitializationException`, bên trong là `ArgumentException` nêu cách sửa.
- Mọi collection trong `Collections` và mọi collection đích của `OutboxProjections` phải có đúng một `Deletion`
  (`NoPlayerData`, `Anonymize`, `Remove`, `ModuleStep` + `DeletionSteps`), nếu không startup fail; `pa_*` do platform xoá
  (slice 010 — `docs/pcr/platform.md` mục 16).
- Kind outbox `analytics` do framework giữ chỗ (slice 012): projection/consumer cùng kind → startup fail. Event analytics
  từ command: `ctx.AddAnalyticsEvent(...)` (`docs/guides/analytics.md`).

Host: `builder.AddGameServer().AddModule<IdentityModule>().AddModule<MyModule>();` — host đầy đủ (chạy bằng
`app.RunGameServerAsync(mode)`, slice 011) xem `samples/SampleHost/Program.cs`.

### Config: file, luật và catalog (slice 015 — FG-018)

Module khai **mọi** file config nó đọc trong descriptor; framework kiểm chúng ở khối đầu tiên của `UseGameServer`, một dòng mỗi
lỗi (module, file, row/key/path, fix), trước mọi traffic. Ví dụ của template (`templates/module/Template.Module/TemplateModuleDefinition.cs`):

```csharp
// Every config file the module reads, with its content rules (checked at every start, FG-018). Use Required(...) for a
// file the module cannot work without.
ConfigFiles = [ConfigFileDeclaration.Optional(ConfigFile, TemplateConfigRules.Points)],

// A typed catalog built once from the files before traffic; handlers take it from DI. Declare it here, never register
// it in ConfigureServices (an undeclared catalog stays lazy and its faults surface on the first request). The file is
// optional, so the build runs without it too and must return an empty catalog then, here with MyCatalog (your catalog type):
// ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => c.Config.HasFile(ConfigFile) ? MyCatalog.Load(c.Config) : MyCatalog.Empty, ConfigFile)],
```

```csharp
public static class TemplateConfigRules
{
    /// <summary>Rule <c>template.points</c>: <c>maxPoints</c> is an integer of at least 1.</summary>
    public static ConfigRule Points { get; } = new("template.points", CheckPoints);

    private static void CheckPoints(ConfigContext context) =>
        ConfigJson.RequiredInteger(
            context.Json(TemplateModuleDefinition.ConfigFile),
            "maxPoints",
            ConfigLocation.WholeFile,
            context.Problems,
            min: 1,
            fix: "set the most points a player may hold, at least 1");
}
```

- `RequiredConfigFiles` = file bắt buộc **không** luật (vẫn dùng được). `ConfigFiles` = `ConfigFileDeclaration.Required(name,
  rules…)` hoặc `Optional(name, rules…)` (file vắng → luật không chạy; template dùng `Optional` để host không có `template.json`
  vẫn start). `ConfigCatalogs` = `ConfigCatalogDeclaration.Of<T>(build, files…)`; file của catalog là bắt buộc trừ khi module
  khai nó `Optional`; file `Optional` vắng thì catalog **vẫn được build**: build tự quyết catalog rỗng (`c.Config.HasFile(file)`
  false → catalog rỗng). Build ném hoặc trả null → một dòng lỗi, host không start (không bao giờ lazy).
- Luật viết bằng `ConfigJson` (`RequiredString`, `RequiredInteger`, `OptionalInteger`, `TryGetObject`, `TryGetArray`,
  `RequireObject`, `PathOf`) để câu chữ giống mọi module (`'maxPoints' is missing`, `… must be an integer of at least 1, found
  "5"`); báo qua `context.Problems.Error/Warning(ConfigLocation…, problem, fix)`, **không ném** (luật ném → một dòng "rule threw").
  "Số nguyên JSON" là `TryGetInt64` (`1.0`, `1e3`, `"5"` là lỗi). Luật đọc lỏng file của module khác (ví dụ
  `ResourceReferences.Read(context)` cho `resources.json`) chỉ so với row hợp lệ.
- Thứ tự tham số của `ConfigJson`: element JSON, tên property, vị trí, `problems`, rồi `out value` (của `TryGetObject`/
  `TryGetArray`), `fix` luôn cuối và tuỳ chọn (viết `fix: "…"`; bỏ trống → `set '{property}' to {type}`). Câu chữ:
  `'{property}' is missing`, `'{property}' is null`, `'{property}' must be {type}, found {kind}`. Vị trí:
  `ConfigLocation.WholeFile`, `Row(n)` (**đếm từ 1**: row đầu là `Row(1)`; `PathOf` đếm từ 0 như JSON path), `Key(key)`,
  `Path("$.a.b")`, `.InFile(file)`. Bảng đủ và một luật row-array hoàn chỉnh (`economy.resources`): consumer guide §12.2.
- Catalog là class thường có hàm build tĩnh trên `IGameConfig` (ví dụ `ResourceCatalog.Load(config)` =
  `config.GetFile<ResourcesFile>(file)`). `GetFile<T>` bind bằng `GameServerJson.Options` (mặc định web của System.Text.Json:
  tên camelCase, không phân biệt hoa thường, enum là chuỗi camelCase, số đọc được từ chuỗi), nên luật chặt hơn binding. Build
  báo lỗi qua `c.Problems` rồi trả null; build ném → dòng `the build threw …`; trả null mà không báo gì → dòng `the build
  returned no catalog …`; cả hai làm host không start (consumer guide §12.3).
- **Khai mọi catalog.** Catalog khai báo được platform build một lần và đăng ký vào DI; module **không** đăng ký nó trong
  `ConfigureServices` (test `ConfigCatalogArchitectureTests` bắt lỗi này ở module của framework, sample và template). Catalog
  không khai báo vẫn lazy: lỗi của nó chỉ lộ ở request đầu tiên đọc nó. Host/test đăng ký instance của kiểu đó (trước hoặc sau
  `AddModule`) thì instance đó thắng.
- Dữ liệu suy ra từ config (bảng tra, index) chỉ để trong catalog khai báo, không trong singleton tự dựng (seam FG-020).
- Startup check của module đọc config qua `GameConfigStatus` (`TryGetConfig`, `TryGetCatalog<T>`, `DeclarationsPassed`),
  không nhận `IGameConfig`, không dựng lại catalog bằng `Load` (PCR platform §17, §21). Đăng ký trong `ConfigureServices` của
  adapter (hoặc ở host): `services.AddSingleton<IStartupCheck, MyCheck>()`, trong `IServiceCollection` (đăng ký chỉ trong
  container builder của bên thứ ba không được thấy và không chạy). Mỗi chuỗi thêm vào `result.Errors`/`Warnings` là một dòng
  của báo cáo, in nguyên văn: viết theo định dạng `[{check hoặc module}] {problem}. Fix: {fix}.` Ví dụ:
  `tests/ConsumerFixture/src/Fixture.Game/NotesResultSizeCheck.cs`.
- Định dạng dòng và thứ tự: PCR platform §21.

### Trạng thái khởi đầu của tài khoản: account initializer (slice 016 — FG-015)

`IAccountInitializer` (namespace `GameServer.Accounts`) ghi trạng thái khởi đầu của tài khoản mới **trong chính transaction
tạo tài khoản**: profile và mọi thứ initializer ghi cùng commit, hoặc không có gì. Ví dụ đầy đủ (comment) ở
`templates/module/Template.Module/TemplateModuleDefinition.cs`; ví dụ chạy được ở
`tests/ConsumerFixture/src/Fixture.Game/FixtureWelcomeInitializer.cs` và `samples/SampleGame/SampleGame/SampleAccountInitializer.cs`.

```csharp
public sealed class MyStartingState : IAccountInitializer
{
    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        context.Set("mything", new MyState { Points = 10 });
        return ValueTask.FromResult<ErrorCode?>(null);   // null = OK
    }
}

// ModuleDescriptor:
AccountInitializers = [AccountInitializerRegistration.Of<MyStartingState>("my.starting_state", ["mything"])],
```

- **Đăng ký**: `ModuleDescriptor.AccountInitializers`; tên `<area>.<name>` chữ thường, duy nhất trong deployment. Initializer
  được tạo mỗi attempt từ DI (constructor injection, như handler). Chạy theo thứ tự đăng ký module rồi thứ tự trong danh sách,
  trên một context chung; chỉ chạy ở lần login đầu tiên không thấy profile.
- **Ngân sách touches**: `Touches` là các aggregate được đọc và ghi, **không bao giờ** `profile`. `profile` + hợp touches của mọi
  initializer ≤ 4 aggregate (`CommandDescriptor.MaxTouches`); tổng claim marker của mọi initializer ≤ 2. `Get`/`Set` ngoài
  touches ném thông báo của runner có tên initializer (`Command 'identity.create_account (initializer my.starting_state)' wrote
  aggregate '…' which is not declared in Touches (DAT-09).`). Sai khai báo → startup fail, một dòng mỗi lỗi (016/BC-9).
- **Luật thuần**: như handler — transaction có thể chạy lại nhiều lần; chỉ đọc context (`Config`, `Now`, `NewId`, `NewSeed`) và
  catalog từ DI; không I/O, không clock, không random riêng.
- **Khi lỗi**: trả mã, ném exception hoặc vi phạm luật commit (kích thước aggregate, audit entry, kích thước receipt) → không
  ghi gì (không profile, aggregate, claim, receipt), login trả 500 `INTERNAL`, một dòng Error category `GameServer.Accounts`
  (`Account creation of player {id} failed: … Nothing was created; the next login retries.`, không bao giờ có message của
  exception), metric `account_initializer_failures_total{initializer, result}` và `account_creations_total{result="failed"}`.
  Login kế tiếp của cùng credential tạo lại từ đầu.
- **Receipt**: chỉ khi initializer có hiệu ứng (đổi aggregate, giữ claim, thêm outbox) thì có một receipt
  `identity.create_account` (actor `system`, actor id = player); outbox của nó (ledger của grant) được relay như command.
- Tài khoản tạo trước khi đăng ký initializer **không** được seed về sau (không backfill): giữ command "init" cũ cho chúng, hoặc
  xoá dữ liệu dev/test trước khi ra mắt.
- **Test bằng kit** (không host): `CommandContextDouble.ForAccountInitializer(name, modules…)` chạy initializer sau view của
  account writer (actor system cho `p_test`, touches ở state mặc định, operation `identity.create_account`);
  `CheckAccountCreation()` trả các dòng luật của writer, rỗng = creation commit (ví dụ: consumer guide §13.2,
  `tests/ConsumerFixture/tests/Fixture.Tests/FixtureInitializerTests.cs`).

## 5. Route và endpoint

### 5.1 Khai báo route

Mỗi route HTTP được khai **một lần**, dạng dữ liệu, trong project nghiệp vụ (cạnh command, không ASP.NET, không Protobuf)
và liệt kê trong `ModuleDescriptor.Routes` (mục 4):

```csharp
public static class MyRoutes
{
    public const string EarnTemplate = "/api/v1/mything/earn";

    public static RouteDeclaration Earn { get; } = RouteDeclaration.Command(EarnHandler.Descriptor, EarnTemplate);
}
```

- `RouteDeclaration.Command(descriptor, template, name?, condition?)`: `POST` chạy command; tên = operation
  (`mything.earn`), profile và permission lấy từ `CommandDescriptor`. Route không phải command:
  `RouteDeclaration.Get/Post/Put/Delete(name, template, profile, surface = PlayerApi, permission, condition)`; probe nhận
  mọi method: `AnyMethod`.
- Luật đặt tên: `<area>.<route>` chữ thường, `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, tối đa 64 ký tự, duy nhất trong host.
  Tên quyết định tên member sinh cho Unity (tách theo `.` và `_`, viết hoa chữ đầu mỗi phần: `mything.earn` →
  `GameRoutes.MythingEarn`, lớp theo `routesClass` của `wiregen.json`, mục 9) và có trong request log (`RouteName`); đổi tên
  là breaking change với client.
- Template có `/` đầu và segment version (`/v{n}/`). Route player-facing (client gọi) chỉ dùng tham số `{name}` đơn giản
  (kể cả trong segment, `{id}.json`): không constraint, optional, default hay catch-all; mỗi khai báo một method.
- Route có `condition: "x"` chỉ được map khi condition `x` đúng; module đăng ký nó bằng
  `services.AddRouteCondition("x", sp => …)`. Có sẵn: `development_tools` (server Development/Test).
- Route trong `PublicEndpoints` phải khai profile `Public`, nếu không startup fail (014/BC-5).

### 5.2 Map từ khai báo và wire mapper

```csharp
public void MapPlayerApi(IEndpointRouteBuilder routes) =>
    routes.MapPlayerCommand<Wire.EarnRequest, EarnCommand, EarnResult>(MyRoutes.Earn, ToDomain, r => new Wire.EarnResult { Points = r.Points });

// Endpoint không phải command (MyRoutes.Status = RouteDeclaration.Post("mything.status", "/api/v1/mything/status", SecurityProfile.Player)):
routes.MapDeclared(MyRoutes.Status, (HttpContext http) => { /* … */ }).AsPlayerEndpoint();

// Section client-visible: wire mapper kèm kiểu message, khai cái server giữ lại có chủ ý.
services.AddAggregateWireMapper<MyState, Wire.MyState>("mything", ToWire, o => o.Hidden("Internal.Note"));
```

- `MapPlayerCommand`/`MapDevCommand`/`MapGmCommand(declaration, …)` lấy method, template, operation, permission từ khai
  báo. `MapDeclared(declaration, handler)` map method + template; vẫn gọi helper `As*Endpoint()` đúng profile đã khai.
  Endpoint do thư viện map (`MapHealthChecks`) gắn khai báo bằng `.WithDeclaration(route)`.
- `o.Hidden("Member", "Nested.Member")`: member của state không bao giờ gửi cho client (tên member C# nối bằng `.`, phần tử
  collection/value của dictionary trong suốt). `o.MayStayDefault("field")`: field proto mà giá trị có nghĩa có thể là
  default (enum 0), tên field proto nối bằng `.`. Đường dẫn không tồn tại → startup fail nêu aggregate và path.
- Overload một kiểu `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` đã obsolete (CS0618 = lỗi build với
  `TreatWarningsAsErrors`): section vẫn chạy nhưng có Warning lúc startup và Unity không có parser.
- Một aggregate một mapper: đăng ký lần hai cùng id → startup fail. Game thay mapper của framework bằng
  `ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(id, map, o => …))` (mục 6).

### 5.3 Kiểm tra endpoint lúc startup

Mọi endpoint phải có đúng một profile (`AsPlayerEndpoint`, `AsGmEndpoint(permission)`, `AsPublicEndpoint` +
allowlist, `AsDevEndpoint` — chỉ khi development tools được phép: local, dev, test; chặt hơn OPS-09) và route có
`/v{n}/`. Validation wire nằm trong `ToDomain` (ném `GameServerException(CoreErrorCodes.BadRequest)`). Operation truyền cho
`MapPlayerCommand`/`MapDevCommand`/`MapGmCommand` phải là command đã khai trong `Commands` của một module đã `AddModule`,
nếu không startup fail: `[endpoint POST /api/v1/mything/earn] runs command 'mything.earn' which no registered module
declares. …`. Profile của route phải đúng profile của command (`MapPlayerCommand` ↔ Player, `MapDevCommand` ↔ Dev,
`MapGmCommand` ↔ Gm) và permission truyền cho `MapGmCommand` phải đúng `CommandDescriptor.Permission`, nếu không startup
fail.

Host so mọi endpoint đã map với catalog route ở mọi môi trường và không phục vụ khi có lỗi (exit 1, mỗi lỗi một dòng nêu
cách sửa): endpoint chưa khai, template/method/profile/command/permission khác khai báo, route khai mà không map. Endpoint
map **sau** `UseGameServer` (trực tiếp trên `app`, trong `MapGroup`) cũng được kiểm cùng luật trước khi server nhận kết
nối. Ví dụ:

```text
[endpoint POST /api/v1/mything/earn] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).
```

Endpoint host tự map (ngoài module) khai bằng `gameServer.DeclareRoutes(...)` trước `Build()` (consumer guide §4).

## 6. Override của game

```csharp
gameServer.ConfigureExtensions(x => x
    .ReplacePolicy<IMyPolicy, GamePolicy>()
    .ReplaceHandler<EarnCommand, EarnResult, GameEarnHandler>("mything.earn")
    .ReplaceWireMapper<MyState, Wire.MyState>("mything", GameToWire, o => o.Hidden("Internal.Note")));
```

Gọi sau `AddModule` của module gốc. Handler thay thế vẫn chạy trong pipeline chung (auth, gate, idempotency,
transaction).

## 7. Dùng module như service

`AddModule<MyModule>(m => m.ServiceOnly())` đăng ký service mà không map endpoint (ARC-04).

## 8. Test

Test kit của framework là project `src/GameServer.TestKit` (namespace `GameServer.TestKit`): `TestHost`,
`LocalFlavorFactory<TEntryPoint>`, `CommandContextDouble`, `TestGameConfig`, `ProtobufHttp`, `IdentityHttp`,
`TestStores`, `GmBrowser`. Store và clock giả nằm trong `GameServer.Platform.Testing` (`InMemoryDocumentStore`,
`FakeClock`, `StoreFaultInjector`). `GAMESERVER_TEST_STORE=File` chạy cùng test trên file store (slice 011).

**Trong repo framework** (module ở `modules/` hoặc `samples/`, test trong `GameServer.slnx`): test project dưới `tests/`
link `tests/Shared/*.cs` và gọi `SampleTestHost.StartAsync(...)` (mặc định Identity + `Sample.Counter`, config mẫu)
— xem `tests/Sample.Counter.Tests`.

**Trong repo game** (framework là submodule, xem [consumer-guide.md](consumer-guide.md) mục 5): test project tham chiếu
kit, không link file nào của `tests/Shared`:

```xml
<ProjectReference Include="$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj" />
<ProjectReference Include="../../src/Fixture.Game/Fixture.Game.csproj" />
<ProjectReference Include="../../src/Fixture.Host/Fixture.Host.csproj" />
```

Handler test với `CommandContextDouble` (không host, store, DI; cùng luật truy cập với runner; ví dụ copy từ
`tests/ConsumerFixture/tests/Fixture.Tests/SaveNoteHandlerTests.cs`):

```csharp
private static CommandContextDouble Context() =>
    CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule()).AsPlayer("p_1").WithConfig(Rules);

[Fact]
public async Task Save_replaces_the_note_and_the_runner_would_commit_it()
{
    var context = Context().Seed(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = "old", Saves = 1 });

    var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("new"), context);

    context.CheckCommit(outcome).ShouldBeEmpty();
    outcome.Result.ShouldBe(new SaveNoteResult("new", 2));
    context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate).ShouldBe(new NotesState { Text = "new", Saves = 2 });
}
```

- `Create(operation, modules…)`: truyền mọi module có aggregate mà command `Touches`/`Reads` (ví dụ `new IdentityModule()`
  cho `profile`). Cấu hình (`AsPlayer`, `AsGm`, `At`, `WithConfig`, `Seed`, `SeedClaim`) trước lần gọi handler đầu tiên.
- `CheckCommit(outcome)` trả các lỗi mà runner sẽ báo khi commit (code không có trong catalog, code commit ≠ HTTP 200,
  aggregate vượt `HardBytes`, thiếu audit GM); rỗng = runner sẽ commit. `CheckCommit(outcome, encodeResult)` kiểm thêm
  giới hạn resultCompact; kích thước receipt không được kiểm (consumer guide §5.1). `Writes` bỏ qua `Set` không đổi state.
- Host test: `TestHost.StartAsync(modules: b => b.AddModule<IdentityModule>().AddModule<MyModule>())` (mặc định chỉ
  Identity, config rỗng; tham số `config:` nhận một `IGameConfig`, ví dụ `TestGameConfig.FromJson(...)`, thay cho config trên
  đĩa); flavor thật của host game: `new LocalFlavorFactory<Program>()` (thư mục tạm riêng, xoá khi dispose). Startup lỗi →
  `TestHost.StartAsync` và `LocalFlavorFactory.StartServer()`/`CreateClient()` ném `GameServerStartupException`, `Violations`
  là báo cáo. Host trên port thật: `LocalFlavorOptions.Settings["Urls"]` (port trống, không dùng port của flavor),
  `Settings["GameServer:ConfigPath"]`, rồi `factory.UseKestrel()` + `factory.StartServer()` (consumer guide §12.4).

**Luật config trong test** (slice 015, không host): `ConfigDeclarationCheck.Check(config, modules…)` cho đúng các dòng mà host
in khi khởi động (ví dụ từ `tests/ConsumerFixture/tests/Fixture.Tests/FixtureConfigTests.cs`):

```csharp
var config = TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = notes }, "fixture-test");

var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule());

report.Passed.ShouldBeFalse();
```

`Errors`, `Warnings`, `Passed`, `ThrowIfFailed()`, `Catalog<T>()`/`TryGetCatalog<T>()`, `AddCatalogsTo(services)` (dựng
services cho test không có host). `FromJson` phải có **mọi** file `Required` của các module truyền vào (thiếu → dòng
`required config file … is missing`): thêm một file `Required` vào module thì sửa luôn các test kit dựng config bằng `FromJson`.

Unit test của startup check: `TestGameConfigStatus.NotLoaded()`, `.Failed()` hoặc `.From(report)` (đúng status host công bố
cho config và module đó), ví dụ `tests/ConsumerFixture/tests/Fixture.Tests/FixtureStartupCheckTests.cs`:

```csharp
var report = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(dir), new IdentityModule(), new FixtureModule());
var result = new StartupCheckResult();

new NotesResultSizeCheck(TestGameConfigStatus.From(report), new GameServerOptions()).Check(result);

result.Errors.ShouldBeEmpty();
```

Test gọi SDK: tham chiếu `sdk/GameServer.Client/GameServer.Client.csproj` với `Aliases="sdk"` và `extern alias sdk;` (SDK và
server biên dịch cùng kiểu protocol sinh ra, thiếu alias → CS0433), dùng `HttpClientTransport` trên `factory.CreateClient()`,
`LoginGuestAsync`, rồi `RefreshConfigAsync()` khi `ConfigOutdated`. Test project ghim danh sách tham chiếu thì thêm SDK (kèm
alias) vào danh sách cho phép. Ví dụ: `tests/ConsumerFixture/tests/Fixture.Tests/FixtureSdkConfigTests.cs` (consumer guide §12.4).

**Test conformance của wire mapper** (bắt buộc cho module có aggregate client-visible): mapper quên một member thì Unity
nhận giá trị default mà không có lỗi nào. `WireConformance` (kit, không host, không store, không test framework) dựng state
mẫu từ JSON contract của state, map, rồi đổi từng member một: field proto còn default → `FieldAtDefault`, member đổi mà
wire không đổi → `MemberNotOnWire`, member khai `Hidden` mà lại lên wire → `HiddenMemberOnWire`.

```csharp
[Fact]
public void The_wire_mappers_pass() =>
    WireConformance.CheckModules([new IdentityModule(), new MyModule()]).ThrowIfFailed();
```

`ThrowIfFailed()` ném `WireConformanceException`, mỗi finding một dòng `[aggregate {id}] {problem}: {path} — {fix}`. Sửa
mapper, hoặc khai `o.Hidden(...)`/`o.MayStayDefault(...)` ở chỗ đăng ký (mục 5.2). Member kit không tự điền được
(`object`, interface) → `MemberNotFillable`: cấp giá trị bằng `new WireConformanceOptions().WithMemberValue(id, path,
value)` (hoặc cả state mẫu bằng `WithSampleState(id, state)`). Bảng giá trị mẫu và cách đổi: consumer guide §5.5. Trong
repo framework, module của framework/sample/template được kiểm trong `tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs`.

## 9. Sinh code Unity

Route, section và message `.proto` của module được sinh sang C# cho Unity bằng `tools/GameServer.WireGen` (không sửa file
nào của framework, không cần item MSBuild nào: file sinh ra được commit):

- **Trong repo framework** (module ở `modules/` hoặc `samples/`, client mẫu KestrelDemo): thêm `.proto` của module vào
  `protos[]` của `samples/SampleGame/wiregen.json`, rồi chạy

  ```text
  dotnet run --project tools/GameServer.WireGen -- generate --config samples/SampleGame/wiregen.json
  ```

  File sinh nằm ở `samples/SampleGame/KestrelDemo/Assets/SampleGame/Generated/` (`SampleGameRoutes.cs`,
  `SampleGameSections.cs`, một file C# mỗi proto). Commit chúng cùng `.meta` Unity tạo khi mở project.
  `CommittedOutputTests` chạy `verify` của config này trong mỗi lần test framework.
- **Trong repo game**: `wiregen.json` riêng của game (consumer guide §10.3), `dotnet run --project
  framework/tools/GameServer.WireGen -c Release -- generate --config <file>`, `verify --no-build` trong CI.

Thêm route vào module là đổi file client: chạy lại `generate` (không thì `verify` và test codegen của game fail). Test codegen
của fixture (`FixtureCodegenTests`) chạy tool đã build: build nó trước bằng `dotnet build tools/GameServer.WireGen` (build cả
solution cũng được).

Client dùng hằng sinh ra thay vì chuỗi copy: `client.SendCommandAsync(SampleGameRoutes.CounterIncrement, …)`,
`client.Sections.Register(SampleGameSections.Table)` một lần, `client.Sections.Get(SampleGameSections.Counter)`.

Cập nhật PCR của module (`docs/pcr/<module>.md`): aggregate, bảng command → Touches/Reads, error codes, capability
thay được, bảng route (tên, method, template, profile) và section (message, hidden member).
