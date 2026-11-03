# Viết module cho Game Server Framework

> Đúng với framework v0.7.0 (chưa phát hành).

Hướng dẫn này dùng `modules/Sample.Counter` làm ví dụ đầy đủ. Template rỗng ở `templates/module/`. Module viết trong repo
game (framework là submodule): xem thêm [consumer-guide.md](consumer-guide.md).

## 1. Hai project

| Project | Chứa | Tham chiếu |
| --- | --- | --- |
| `MyModule` | state aggregate, command/result (domain), handler, policy, error codes, `ModuleDescriptor` | chỉ `GameServer.Core` |
| `MyModule.Adapters` | `IGameModule` + `IPlayerApiModule`/`IGmApiModule`, `.proto`, mapping wire ↔ domain, wire mapper, service implementation | module + `GameServer.Platform.Web` |

Arch test chặn project nghiệp vụ tham chiếu HTTP, Couchbase, Protobuf hoặc `GameServer.Platform` (ARC-01).

## 2. Aggregate

```csharp
public sealed record MyState { public long Points { get; init; } }

public static PlayerAggregateDescriptor<MyState> Descriptor { get; } =
    new("mything", schemaVersion: 1, clientVisible: true, createDefault: () => new MyState());
```

- Id `[a-z][a-z0-9_]{1,31}`, lưu ở collection `pa_mything`, key = `playerId`.
- Tăng `schemaVersion` thì thêm một `IAggregateUpgrader` cho mỗi bước (`DelegateAggregateUpgrader`).
- Client-visible thì adapter phải đăng ký `services.AddAggregateWireMapper<MyState>("mything", s => new Wire.MyState {...})`.

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

## 5. Endpoint

```csharp
public void MapPlayerApi(IEndpointRouteBuilder routes) =>
    routes.MapPlayerCommand<Wire.EarnRequest, EarnCommand, EarnResult>(
        "/api/v1/mything/earn", "mything.earn", ToDomain, r => new Wire.EarnResult { Points = r.Points });
```

Mọi endpoint phải có đúng một profile (`AsPlayerEndpoint`, `AsGmEndpoint(permission)`, `AsPublicEndpoint` +
allowlist, `AsDevEndpoint` — chỉ khi development tools được phép: local, dev, test; chặt hơn OPS-09) và route có `/v{n}/`. Validation wire nằm trong `ToDomain`
(ném `GameServerException(CoreErrorCodes.BadRequest)`). Operation truyền cho `MapPlayerCommand`/`MapDevCommand`/
`MapGmCommand` phải là command đã khai trong `Commands` của một module đã `AddModule`, nếu không startup fail:
`[endpoint POST /api/v1/mything/earn] runs command 'mything.earn' which no registered module declares. …`. Profile của
route phải đúng profile của command (`MapPlayerCommand` ↔ Player, `MapDevCommand` ↔ Dev, `MapGmCommand` ↔ Gm) và
permission truyền cho `MapGmCommand` phải đúng `CommandDescriptor.Permission`, nếu không startup fail.

## 6. Override của game

```csharp
gameServer.ConfigureExtensions(x => x
    .ReplacePolicy<IMyPolicy, GamePolicy>()
    .ReplaceHandler<EarnCommand, EarnResult, GameEarnHandler>("mything.earn"));
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
  thiếu audit GM); rỗng = runner sẽ commit.
- Host test: `TestHost.StartAsync(modules: b => b.AddModule<IdentityModule>().AddModule<MyModule>())` (mặc định chỉ
  Identity, config rỗng); flavor thật của host game: `new LocalFlavorFactory<Program>()` (thư mục tạm riêng, xoá khi
  dispose).

Cập nhật PCR của module (`docs/pcr/<module>.md`): aggregate, bảng command → Touches/Reads, error codes, capability
thay được.
