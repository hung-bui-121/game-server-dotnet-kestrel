# Viết module cho Game Server Framework

Hướng dẫn này dùng `modules/Sample.Counter` làm ví dụ đầy đủ. Template rỗng ở `templates/module/`.

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
trong `Touches`; dùng `ctx.Now` (không `DateTime.UtcNow` — analyzer chặn); hiệu ứng "một lần" dùng
`ctx.TryClaimAsync(collection, key)`. Reject → không ghi gì, không receipt.

## 4. Descriptor và đăng ký

```csharp
public ModuleDescriptor Describe() => new()
{
    Name = "my.module",
    DependsOn = ["identity"],
    Permissions = ["mything.read"],
    Aggregates = [Descriptor],
    Commands = [Command.Define<EarnCommand, EarnResult>(EarnHandler.Descriptor).HandledBy<EarnHandler>()],
    ErrorCodes = new ErrorCodeSet(11000, 11099, MyErrors.NotAllowed),
    OutboxProjections = [new OutboxProjection("mything_log", "mything_log")],
    Collections = [new CollectionSpec("mything_log")],
};
```

Host: `builder.AddGameServer().AddModule<IdentityModule>().AddModule<MyModule>();`

## 5. Endpoint

```csharp
public void MapPlayerApi(IEndpointRouteBuilder routes) =>
    routes.MapPlayerCommand<Wire.EarnRequest, EarnCommand, EarnResult>(
        "/api/v1/mything/earn", "mything.earn", ToDomain, r => new Wire.EarnResult { Points = r.Points });
```

Mọi endpoint phải có đúng một profile (`AsPlayerEndpoint`, `AsGmEndpoint(permission)`, `AsPublicEndpoint` +
allowlist, `AsDevEndpoint` — cấm ở Production) và route có `/v{n}/`. Validation wire nằm trong `ToDomain`
(ném `GameServerException(CoreErrorCodes.BadRequest)`).

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

- Unit: `GameServer.Platform.Testing` (`InMemoryDocumentStore`, `FakeClock`, `StoreFaultInjector`).
- HTTP: xem `tests/Shared/TestHost.cs`.
- Cập nhật PCR của module (`docs/pcr/<module>.md`): aggregate, bảng command → Touches/Reads, error codes,
  capability thay được.
