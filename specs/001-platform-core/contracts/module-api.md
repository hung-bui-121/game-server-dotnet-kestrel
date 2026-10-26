# Module authoring contract — slice 001

Contract C# công khai mà module của game dùng. Tên là bắt buộc; chữ ký chi tiết có thể tinh chỉnh
lúc implement nhưng phải giữ ngữ nghĩa dưới đây.

## Đăng ký (GameServer.Core)

```csharp
public interface IGameModule
{
    ModuleDescriptor Describe();                       // tên, version, dependency, config, permission,
                                                       // collection, aggregate, command, error code
    void ConfigureServices(IServiceCollection services, ModuleContext context);
}

public interface IPlayerApiModule { void MapPlayerApi(IPlayerRouteBuilder routes); }  // Platform.Web
public interface IGmApiModule     { void MapGmApi(IGmRouteBuilder routes); }          // Platform.Web
```

Host (composition root):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddGameServer(o => o.UseCouchbase())          // hoặc o.UseInMemoryStore() cho test/dev
       .AddModule<IdentityProfileModule>()
       .AddModule<CounterModule>()                    // một lệnh / module (ARC-03)
       .AddModule<CounterModule>(m => m.ServiceOnly()) // không map endpoint (ARC-04)
       .ConfigureExtensions(x => x.ReplacePolicy<ICounterLimitPolicy, GameCounterLimitPolicy>());
var app = builder.Build();
app.UseGameServer();       // middleware theo thứ tự R11 + map endpoint + startup validation
await app.RunAsync();
```

`ConfigureExtensions` chạy sau mọi framework module (EXT-02). `ReplaceHandler(operation, handler)`
thay handler của command.

## Aggregate

```csharp
public sealed record PlayerAggregateDescriptor<TState>(
    string AggregateId, int SchemaVersion, bool ClientVisible,
    Func<TState> CreateDefault,
    IReadOnlyList<IAggregateUpgrader> Upgraders,   // JsonObject from vN -> vN+1
    int WarnBytes = 32 * 1024, int HardBytes = 64 * 1024);
```

Wire mapping (adapter): `IAggregateWireMapper<TState>` → `IMessage`.

## Command

```csharp
public sealed record CommandDescriptor<TRequest, TResult>(
    string Operation,                       // "counter.increment"
    IReadOnlyList<string> Touches,          // 1..4 aggregateId
    IReadOnlyList<string> Reads,
    int MaxClaimMarkers);                   // 0..2

public interface ICommandHandler<TRequest, TResult>
{
    ValueTask<CommandOutcome<TResult>> HandleAsync(TRequest request, ICommandContext ctx);
}

public interface ICommandContext
{
    ActorContext Actor { get; }
    IClock Clock { get; }
    string ConfigVersion { get; }
    IGameConfig Config { get; }
    TState Get<TState>(string aggregateId);              // đã nạp bằng GetMany
    void Set<TState>(string aggregateId, TState state);  // chỉ aggregate trong Touches
    ValueTask<bool> TryClaimAsync(string collection, string claimKey); // false = đã tồn tại
    void AddOutbox(string kind, object payload);
}
// CommandOutcome<TResult>.Ok(result) | .Reject(ErrorCode)
```

Handler không có side effect ngoài `ICommandContext`; có thể bị gọi lại nhiều lần.

## Runner (GameServer.Platform)

```csharp
public interface ICommandRunner
{
    Task<CommandExecution> ExecuteAsync(CommandInvocation invocation, CancellationToken requestAborted);
}
```

`requestAborted` chỉ dùng trước bước commit (PERF-02).

## Store abstraction (GameServer.Platform; impl ở Platform.Couchbase và Platform.Testing)

```csharp
public interface ITransactionalDocumentStore
{
    Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options);
    Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken ct);           // ngoài txn
    Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken ct);
    Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken ct); // projection
    Task MarkOutboxRelayedAsync(string receiptId, CancellationToken ct);
    IAsyncEnumerable<StoredDocument> QueryPendingOutboxAsync(DateTimeOffset olderThan, int limit, CancellationToken ct);
}

public interface ITransactionContext
{
    Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys);
    Task InsertAsync(DocumentKey key, JsonObject content);
    Task ReplaceAsync(StoredDocument existing, JsonObject content);
}
// Lỗi chuẩn hoá: TransactionCommitAmbiguousException, TransactionConflictExpiredException,
// TransactionFailedException (namespace GameServer.Platform.Persistence).
```

Chỉ `GameServer.Platform` (runner, writer, outbox) được dùng `ITransactionalDocumentStore` để ghi
`pa_*`/`receipts` — kiểm bằng arch test.

## Error code

```csharp
public sealed record ErrorCode(int Number, string Name, int HttpStatus, bool Retryable, string SafeMessage);
// ModuleDescriptor.ErrorCodes = new ErrorCodeSet(rangeStart: 10000, rangeEnd: 10099, codes...)
```
