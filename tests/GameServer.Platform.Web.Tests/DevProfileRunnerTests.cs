using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GameServer.Actors;
using GameServer.Errors;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Registry;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Layer 3 of FG-010 alone (FR-005, FR-007, research R4, R5, plan SD7): a Development host whose singleton options are
/// switched to Staging after startup ("validator bypassed"); the runner refuses Dev commands on the execute and the replay
/// path for every actor type, before the limiter, the receipt read and the transaction.
/// </summary>
public sealed class DevProfileRunnerTests
{
    private const string PlayerId = "p1";

    public static TheoryData<ActorType> Actors => new() { ActorType.Player, ActorType.Gm, ActorType.System };

    private static Task<TestHost> StartAsync() =>
        TestHost.StartAsync(modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule<CheatModule>());

    private static void SwitchToStaging(TestHost host)
    {
        var options = host.Services.GetRequiredService<GameServerOptions>();
        options.DeploymentEnvironment = "Staging";
        options.EnvironmentId = "beta";
    }

    private static ActorContext Actor(ActorType type) =>
        new(type == ActorType.Player ? PlayerId : "actor-" + type, type, PlayerId, "test", "test", new HashSet<string>(), "corr");

    private static CommandInvocation Grant(ActorType type, string requestId) =>
        new(CheatGrantHandler.Operation, Actor(type), requestId, "hash", new CheatGrantCommand("gold", 5), _ => [1]);

    private static int Documents(TestHost host) =>
        host.Services.GetRequiredService<ModuleRegistry>().RequiredCollections().Sum(host.Store.Count);

    [Theory]
    [MemberData(nameof(Actors))]
    public async Task Execute_and_replay_of_a_dev_command_are_forbidden_outside_development(ActorType type)
    {
        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        SwitchToStaging(host);
        var before = Documents(host);
        using var refusals = new RefusalCounter();

        var execute = await Should.ThrowAsync<GameServerException>(() => runner.ExecuteAsync(Grant(type, Guid.NewGuid().ToString("D")), CancellationToken.None));
        var replay = await Should.ThrowAsync<GameServerException>(() =>
            runner.TryReplayAsync(CheatGrantHandler.Operation, Actor(type), Guid.NewGuid().ToString("D"), "hash", CancellationToken.None));

        foreach (var refusal in new[] { execute, replay })
        {
            refusal.Code.ShouldBe(CoreErrorCodes.Forbidden);
            refusal.Message.ShouldBe("Dev command 'cheat.grant' refused in deployment environment 'Staging' (environment id 'beta').");
        }

        Documents(host).ShouldBe(before);
        refusals.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_dev_receipt_committed_in_development_is_not_replayed_after_the_switch()
    {
        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        var requestId = Guid.NewGuid().ToString("D");
        var committed = await runner.ExecuteAsync(Grant(ActorType.Player, requestId), CancellationToken.None);
        committed.Code.ShouldBe(CoreErrorCodes.Ok);
        (await runner.TryReplayAsync(CheatGrantHandler.Operation, Actor(ActorType.Player), requestId, "hash", CancellationToken.None))
            .ShouldNotBeNull().Replayed.ShouldBeTrue();

        SwitchToStaging(host);
        var before = Documents(host);

        (await Should.ThrowAsync<GameServerException>(() =>
            runner.TryReplayAsync(CheatGrantHandler.Operation, Actor(ActorType.Player), requestId, "hash", CancellationToken.None)))
            .Code.ShouldBe(CoreErrorCodes.Forbidden);
        (await Should.ThrowAsync<GameServerException>(() => runner.ExecuteAsync(Grant(ActorType.Player, requestId), CancellationToken.None)))
            .Code.ShouldBe(CoreErrorCodes.Forbidden);
        Documents(host).ShouldBe(before);
    }

    [Theory]
    [MemberData(nameof(Actors))]
    public async Task Dev_commands_run_for_every_actor_type_in_development(ActorType type)
    {
        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();

        var execution = await runner.ExecuteAsync(Grant(type, Guid.NewGuid().ToString("D")), CancellationToken.None);

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
    }

    /// <summary>
    /// <c>command_results_total</c> measurements for <c>cheat.grant</c> with code FORBIDDEN, not replayed. Only this class makes
    /// the runner refuse that command (the middleware tests are refused before the runner), so parallel hosts cannot add to it.
    /// </summary>
    private sealed class RefusalCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<long> _values = new();

        public RefusalCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name == "command_results_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? operation = null;
                string? code = null;
                bool? replayed = null;
                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "operation":
                            operation = tag.Value as string;
                            break;
                        case "code":
                            code = tag.Value as string;
                            break;
                        case "replayed":
                            replayed = tag.Value as bool?;
                            break;
                    }
                }

                if (operation == CheatGrantHandler.Operation && code == CoreErrorCodes.Forbidden.Name && replayed == false)
                {
                    _values.Enqueue(value);
                }
            });
            _listener.Start();
        }

        public long Count => _values.Sum();

        public void Dispose() => _listener.Dispose();
    }
}
