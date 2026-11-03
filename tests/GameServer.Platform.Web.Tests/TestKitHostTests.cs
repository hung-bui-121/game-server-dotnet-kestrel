using System.Collections.Concurrent;
using System.Net;
using GameServer.Config;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Hosting;
using GameServer.Protocol.Core.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T025 (FR-011, US2-2): the kit <see cref="TestHost"/> a game uses directly — Identity only, empty game config,
/// file store switch, structured log entries and two instances on one store.
/// </summary>
public sealed class TestKitHostTests
{
    [Fact]
    public async Task Without_arguments_the_host_runs_identity_only_on_the_empty_test_config()
    {
        await using var host = await TestHost.StartAsync();

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var counter = await host.BearerClient(login.SessionToken)
            .PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());

        counter.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var config = host.Services.GetRequiredService<IGameConfig>();
        config.Label.ShouldBe("test");
        config.FileNames.ShouldBeEmpty();
        config.ConfigVersion.ShouldBe(TestGameConfig.Empty.ConfigVersion);
    }

    [Fact]
    public async Task The_config_path_setting_loads_files_and_the_config_argument_wins_over_it()
    {
        var sampleConfig = Path.Combine(AppContext.BaseDirectory, "config");
        var own = TestGameConfig.FromJson(new Dictionary<string, string> { ["rules.json"] = "{\"max\":3}" }, "mine");

        await using var fromPath = await TestHost.StartAsync(settings: s => s["GameServer:ConfigPath"] = sampleConfig);
        await using var fromArgument = await TestHost.StartAsync(settings: s => s["GameServer:ConfigPath"] = sampleConfig, config: own);

        fromPath.Services.GetRequiredService<IGameConfig>().ConfigVersion.ShouldBe(TestGameConfig.FromDirectory(sampleConfig).ConfigVersion);
        fromArgument.Services.GetRequiredService<IGameConfig>().ShouldBeSameAs(own);
    }

    [Fact]
    public async Task Store_kind_file_runs_on_a_temporary_folder_deleted_on_dispose()
    {
        string folder;
        await using (var host = await TestHost.StartAsync(storeKind: TestStoreKind.File))
        {
            folder = host.Services.GetRequiredService<FileDocumentStore>().Folder;
            Path.GetDirectoryName(folder).ShouldBe(Path.GetDirectoryName(TestStores.NewFolder()));
            await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            Directory.EnumerateFiles(Path.Combine(folder, "g_test", "sessions")).ShouldNotBeEmpty();
        }

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task Log_entries_carry_category_level_event_id_and_properties_of_a_structured_call()
    {
        await using var host = await TestHost.StartAsync();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TestKit.Probe");

        logger.LogWarning(new EventId(42, "Probe"), "Value {Value} for {Name}", 7, "x");

        var entry = host.LogEntries.Single(e => e.Category == "TestKit.Probe");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(new EventId(42, "Probe"));
        entry.Message.ShouldBe("Value 7 for x");
        entry.Properties["Value"].ShouldBe(7);
        entry.Properties["Name"].ShouldBe("x");
        entry.Properties["{OriginalFormat}"].ShouldBe("Value {Value} for {Name}");
        entry.Exception.ShouldBeNull();
        host.Logs.ShouldContain("Value 7 for x");
    }

    [Fact]
    public async Task Two_hosts_sharing_store_and_clock_see_each_others_writes()
    {
        await using var first = await TestHost.StartAsync(storeKind: TestStoreKind.InMemory);
        await using var second = await TestHost.StartAsync(store: first.Store, clock: first.Clock);

        var login = await first.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var snapshot = await second.BearerClient(login.SessionToken).PostProtobufAsync(CoreEndpoints.SnapshotRoute, new SnapshotRequest());

        snapshot.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Clock.ShouldBeSameAs(first.Clock);
        second.Store.ShouldBeSameAs(first.Store);
    }

    /// <summary>T060 (FR-052): one record per host, the same through the builder and DI, with the configured instance id.</summary>
    [Fact]
    public async Task The_runtime_info_of_the_builder_is_the_registered_singleton()
    {
        GameServerRuntimeInfo? fromBuilder = null;
        await using var host = await TestHost.StartAsync(
            modules: b =>
            {
                fromBuilder = b.Runtime;
                TestHost.DefaultModules(b);
            },
            settings: s => s["Observability:InstanceId"] = "host-1");

        var runtime = host.Services.GetRequiredService<GameServerRuntimeInfo>();

        runtime.ShouldBeSameAs(fromBuilder);
        runtime.ShouldBe(new GameServerRuntimeInfo(
            "test",
            "test",
            "Development",
            "host-1",
            typeof(TestKitHostTests).Assembly.GetName().Name!,
            GameServerVersion.InformationalOf(typeof(TestKitHostTests).Assembly),
            GameServerVersion.FrameworkInformational));
    }

    /// <summary>T062 (FR-054, US6-3): exactly one Information line after a successful validation, with the five identity values.</summary>
    [Fact]
    public async Task A_started_host_logs_one_startup_line_with_game_framework_environment_and_instance()
    {
        await using var host = await TestHost.StartAsync(settings: s => s["Observability:InstanceId"] = "host-1");
        var runtime = host.Services.GetRequiredService<GameServerRuntimeInfo>();

        var line = host.LogEntries.Single(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Level == LogLevel.Information
            && e.Message.StartsWith("Starting ", StringComparison.Ordinal));

        line.Properties["{OriginalFormat}"].ShouldBe(
            "Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}), instance {InstanceId}.");
        line.Properties["GameId"].ShouldBe("test");
        line.Properties["GameVersion"].ShouldBe(runtime.GameVersion);
        line.Properties["FrameworkVersion"].ShouldBe(GameServerVersion.FrameworkInformational);
        line.Properties["EnvironmentId"].ShouldBe("test");
        line.Properties["DeploymentEnvironment"].ShouldBe("Development");
        line.Properties["InstanceId"].ShouldBe("host-1");
        line.Message.ShouldBe(
            $"Starting test {runtime.GameVersion} on framework {GameServerVersion.FrameworkInformational}: environment test (Development), instance host-1.");
    }

    [Fact]
    public async Task A_failed_validation_logs_no_startup_line()
    {
        var entries = new ConcurrentQueue<(string Category, string Message)>();
        await using var app = TestHost.BuildApp(
            settings: s => s["GameServer:Gate:CacheSeconds"] = "11",
            services: s => s.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(entries)));

        Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        entries.ShouldNotContain(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Message.StartsWith("Starting ", StringComparison.Ordinal));
    }

    /// <summary>Slice 014 T035: an otherwise valid configuration reports its endpoint faults when the host starts, before the start line.</summary>
    [Fact]
    public async Task An_endpoint_fault_fails_the_start_and_logs_no_startup_line()
    {
        var entries = new ConcurrentQueue<(string Category, string Message)>();

        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(
                beforeUseGameServer: app => app.MapGet(UndeclaredRoute, () => "x"),
                services: s => s.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(entries)));
        });

        ex.Message.ShouldContain($"[endpoint GET {UndeclaredRoute}] is not declared in the route catalog.");
        entries.ShouldNotContain(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Message.StartsWith("Starting ", StringComparison.Ordinal));
    }

    /// <summary>Slice 014 T035 (FR-009 kept): with another fault, UseGameServer still gives one report with the endpoint lines.</summary>
    [Fact]
    public async Task A_configuration_fault_and_an_endpoint_fault_give_one_report()
    {
        await using var app = TestHost.BuildApp(settings: s => s["GameServer:Gate:CacheSeconds"] = "11");
        app.MapGet(UndeclaredRoute, () => "x");

        var ex = Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        ex.Violations.ShouldContain("[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).");
        ex.Violations.ShouldContain(v => v.StartsWith($"[endpoint GET {UndeclaredRoute}] is not declared in the route catalog.", StringComparison.Ordinal));
    }

    private const string UndeclaredRoute = "/api/v1/probe/undeclared";

    private sealed class RecordingLoggerProvider(ConcurrentQueue<(string Category, string Message)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<(string Category, string Message)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue((category, formatter(state, exception)));
    }
}

/// <summary>The variable is process-wide: the class runs alone (another test reading it mid-run would switch store).</summary>
[Collection(TestStoreVariableCollection.Name)]
public sealed class TestKitHostStoreVariableTests
{
    [Fact]
    public async Task The_store_variable_switches_a_host_without_store_kind_to_a_temporary_file_store()
    {
        var previous = Environment.GetEnvironmentVariable(TestStores.Variable);
        Environment.SetEnvironmentVariable(TestStores.Variable, "File");
        try
        {
            string folder;
            await using (var host = await TestHost.StartAsync())
            {
                folder = host.Services.GetRequiredService<FileDocumentStore>().Folder;
                Directory.Exists(folder).ShouldBeTrue();
            }

            Directory.Exists(folder).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestStores.Variable, previous);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestStoreVariableCollection
{
    public const string Name = "Test store variable";
}
