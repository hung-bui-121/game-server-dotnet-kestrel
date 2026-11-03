using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Registry;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T015: the shared test support works in a host — <see cref="StartingStateModule"/> starts with Identity + Economy,
/// the <see cref="BrokenDocuments"/> seeds are what they claim to be, and <see cref="CapturedLogSink"/> records the host's lines.
/// </summary>
public sealed class SharedTestSupportTests
{
    private const string PlayerId = "p_shared_support";

    private static Task<TestHost> StartAsync(CapturedLogSink? sink = null) =>
        SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()),
            services: s =>
            {
                if (sink is not null)
                {
                    s.AddSingleton<ILoggerProvider>(sink);
                }
            });

    [Fact]
    public async Task The_starting_state_module_starts_with_identity_and_economy_and_does_nothing_by_default()
    {
        await using var host = await StartAsync();

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        registry.Aggregates.Keys.ShouldContain(StartingStateAggregates.Starter);
        registry.Aggregates.Keys.ShouldContain(StartingStateAggregates.BrokenProbe);
        var initializer = registry.Modules.Single(m => m.Descriptor.Name == StartingStateModule.ModuleName).Descriptor.AccountInitializers.ShouldHaveSingleItem();
        initializer.Name.ShouldBe(StartingStateModule.InitializerName);
        initializer.Touches.ShouldBe([EconomyAggregates.Wallet, StartingStateAggregates.Starter]);
        var behaviour = host.Services.GetRequiredService<StartingStateBehaviour>();
        behaviour.Mode.ShouldBe(StartingStateMode.Nothing);
        behaviour.Invocations.ShouldBe(0);
    }

    [Theory]
    [InlineData(BrokenReason.VersionAhead, "starter", 2, false)]
    [InlineData(BrokenReason.Unreadable, "starter", 1, false)]
    [InlineData(BrokenReason.UpgradeFailed, "broken_probe", 1, false)]
    [InlineData(BrokenReason.MapperFailed, "starter", 1, true)]
    public async Task Each_broken_seed_is_stored_raw_and_its_twin_loads(BrokenReason reason, string aggregate, int storedSchema, bool damagedLoads)
    {
        await using var host = await StartAsync();
        var serializer = host.Services.GetRequiredService<AggregateDocumentSerializer>();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var descriptor = registry.GetAggregate(aggregate);

        var key = BrokenDocuments.Seed(host.Store, reason, PlayerId);
        var raw = host.Store.Peek(key).ShouldNotBeNull();
        key.Collection.ShouldBe("pa_" + aggregate);
        raw["schemaVersion"]!.GetValue<int>().ShouldBe(storedSchema);
        raw["rev"]!.GetValue<long>().ShouldBe(BrokenDocuments.Rev);

        var damaged = await host.Store.GetAsync(key, CancellationToken.None);
        if (damagedLoads)
        {
            var loaded = serializer.Load(descriptor, PlayerId, damaged);
            Should.Throw<InvalidOperationException>(() => StartingStateModule.StarterToWire((StarterState)loaded.State)).Message.ShouldContain("test marker");
        }
        else
        {
            // Today's loader throws for these (typed by T034); the seed only has to be broken.
            Should.Throw<Exception>(() => serializer.Load(descriptor, PlayerId, damaged));
        }

        BrokenDocuments.Seed(host.Store, reason, PlayerId, damaged: false);
        var twin = serializer.Load(descriptor, PlayerId, await host.Store.GetAsync(key, CancellationToken.None));
        twin.Rev.ShouldBe(BrokenDocuments.Rev);
        if (aggregate == StartingStateAggregates.Starter)
        {
            ((StarterState)twin.State).Value.ShouldBe(BrokenDocuments.TwinValue);
            StartingStateModule.StarterToWire((StarterState)twin.State).Value.ShouldBe(BrokenDocuments.TwinValue);
        }
        else
        {
            ((BrokenProbeState)twin.State).Value.ShouldBe(BrokenDocuments.TwinValue);
        }
    }

    [Fact]
    public async Task The_log_sink_records_the_startup_line_like_the_host_capture()
    {
        var sink = new CapturedLogSink();
        await using var host = await StartAsync(sink);

        sink.Lines("GameServer.Startup").ShouldContain(line => line.StartsWith("Starting test ", StringComparison.Ordinal));
        sink.Count(LogLevel.Information, "Starting test ").ShouldBe(1);
        host.LogEntries.Count(LogLevel.Information, "Starting test ").ShouldBe(1);
    }
}
