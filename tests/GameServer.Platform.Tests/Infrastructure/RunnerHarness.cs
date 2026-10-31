using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GameServer.Actors;
using GameServer.Modules;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Testing;
using GameServer.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>Wires the platform runtime on the in-memory store without ASP.NET Core.</summary>
public sealed class RunnerHarness : IDisposable
{
    public RunnerHarness(Action<GameServerOptions>? configure = null, params IGameModule[] extraModules)
        : this(configure, null, extraModules)
    {
    }

    /// <summary>Uses <paramref name="externalStore"/> (e.g. Couchbase) instead of the in-memory store.</summary>
    public RunnerHarness(Action<GameServerOptions>? configure, ITransactionalDocumentStore? externalStore, params IGameModule[] extraModules)
    {
        Options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        Options.Outbox.SweeperEnabled = false;
        configure?.Invoke(Options);

        Registry = new ModuleRegistry();
        var services = new ServiceCollection();
        var context = new ModuleContext(Options.GameId, Options.EnvironmentId, Options.DeploymentEnvironment);
        foreach (var module in new IGameModule[] { TestModules.Identity(), TestModules.Counter(), TestModules.Probe() }.Concat(extraModules))
        {
            Registry.Add(module, serviceOnly: false);
            module.ConfigureServices(services, context);
        }

        Services = services.BuildServiceProvider();
        Store = new InMemoryDocumentStore(Clock);
        Store.UseCollections(Registry.RequiredCollections());
        TransactionalStore = externalStore ?? Store;
        Config = new GameConfig("test", "test+000000000000", new Dictionary<string, byte[]>(), new HashSet<string>());
        Metrics = new PlatformMetrics();
        Serializer = new AggregateDocumentSerializer(Options, Metrics);
        Relay = new OutboxRelay([], Registry, TransactionalStore, Clock, Options, Metrics, NullLogger<OutboxRelay>.Instance);
        Runner = CreateRunner();
        Sweeper = new OutboxSweeper(TransactionalStore, Relay, Clock, Options, Metrics, NullLogger<OutboxSweeper>.Instance);
        Snapshots = new PlayerSnapshotReader(Registry, TransactionalStore, Serializer);
    }

    public GameServerOptions Options { get; }

    public ModuleRegistry Registry { get; }

    /// <summary>In-memory store (only meaningful when no external store was given).</summary>
    public InMemoryDocumentStore Store { get; }

    /// <summary>Store used by the runner: external if given, otherwise <see cref="Store"/>.</summary>
    public ITransactionalDocumentStore TransactionalStore { get; }

    public FakeClock Clock { get; } = new();

    public GameConfig Config { get; }

    public PlatformMetrics Metrics { get; }

    public ServiceProvider Services { get; }

    public AggregateDocumentSerializer Serializer { get; }

    public OutboxRelay Relay { get; }

    public CommandRunner Runner { get; }

    public OutboxSweeper Sweeper { get; }

    public PlayerSnapshotReader Snapshots { get; }

    public List<ICommandGuard> Guards { get; } = [];

    /// <summary>Messages logged by every runner of this harness, as "Level: message".</summary>
    public ConcurrentQueue<string> RunnerLogs { get; } = new();

    /// <summary>A runner with its own per-player limiter, as on a second instance sharing the store.</summary>
    public CommandRunner CreateRunner() =>
        new(Registry, TransactionalStore, Serializer, Config, Clock, Options, new PerPlayerCommandLimiter(Options), Relay, Metrics, Guards, Services, new QueueLogger(RunnerLogs));

    public static ActorContext Player(string playerId) =>
        new(playerId, ActorType.Player, playerId, "test", "dev", new HashSet<string>(), "corr-" + playerId);

    public Task<CommandExecution> RunAsync(
        string operation,
        object request,
        string playerId = "p1",
        string? requestId = null,
        string? body = null,
        CommandRunner? runner = null,
        Func<object, byte[]>? encode = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body ?? JsonSerializer.Serialize(request, request.GetType()));
        var invocation = new CommandInvocation(
            operation,
            Player(playerId),
            requestId ?? Guid.NewGuid().ToString("D"),
            ReceiptIds.BodyHash(bodyBytes),
            request,
            encode ?? (result => JsonSerializer.SerializeToUtf8Bytes(result, result.GetType())));
        return (runner ?? Runner).ExecuteAsync(invocation, CancellationToken.None);
    }

    public void Dispose()
    {
        Services.Dispose();
        Metrics.Dispose();
    }

    private sealed class QueueLogger(ConcurrentQueue<string> logs) : ILogger<CommandRunner>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
