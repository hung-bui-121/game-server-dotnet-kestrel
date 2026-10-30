using System.Net;
using GameServer.Analytics;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Outbox;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// The extension seam (T051, US5-4, FR-002, FR-040; "extension point, preview"): a custom event committed by a game command,
/// a second sink receiving every record, and the reserved outbox kind. The IAP handler and the test module compile against Core
/// only; the ARC-01 architecture rule (business assemblies depend on no GameServer.Platform*) is the proof of US5-5.
/// </summary>
public sealed class AnalyticsSeamTests
{
    private const string Route = "/api/v1/test/seam-emit";

    private static Task<TestHost> StartAsync(InMemoryAnalyticsSink second) =>
        TestHost.StartAsync(
            modules: b =>
            {
                GameHttp.FullGame(b);
                b.AddModule(new SeamModule());
            },
            afterUseGameServer: app => app.MapPlayerCommand<Wire.IncrementRequest, SeamCommand, SeamResult>(
                Route,
                SeamHandler.Operation,
                request => new SeamCommand(request.By),
                result => new Wire.IncrementResult { Value = result.Level }),
            services: s => s.AddSingleton<IAnalyticsSink>(second));

    [Fact]
    public async Task A_custom_event_from_a_game_command_gets_the_envelope_and_a_second_sink_gets_identical_bytes()
    {
        var second = new InMemoryAnalyticsSink { Name = "second" };
        await using var host = await StartAsync(second);
        var (player, token) = await host.NewPlayerAsync();

        var command = await player.CommandAsync(Route, new Wire.IncrementRequest { By = 3 });
        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.BearerClient(token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.FlushAnalyticsAsync();

        var custom = host.Analytics.OfEvent("test_event").ShouldHaveSingleItem();
        custom.Select(p => p.Key).ShouldBe(
            ["event", "schemaVersion", "eventId", "gameId", "environmentId", "userID", "eventTime", "eventTimeUtc", "timeZone", "utcOffset", "receiptId", "level", "label"]);
        custom["receiptId"]!.GetValue<string>().ShouldBe(command.ReceiptId);
        custom["userID"]!.GetValue<string>().ShouldBe(player.PlayerId);
        custom["level"]!.GetValue<int>().ShouldBe(3);

        // payment, login, resume and the custom event: the same bytes in both sinks.
        host.Analytics.Records.Select(r => r["event"]!.GetValue<string>() + "/" + (r["source"]?.GetValue<string>() ?? "-")).Order()
            .ShouldBe(["login/login", "login/resume", "payment/-", "test_event/-"]);
        second.Lines.ShouldBe(host.Analytics.Lines);
    }

    [Fact]
    public async Task A_failing_second_sink_leaves_the_payment_pending_but_never_fails_a_login()
    {
        var second = new InMemoryAnalyticsSink { Name = "second", FailWrites = true };
        await using var host = await StartAsync(second);

        var (player, _) = await host.NewPlayerAsync();
        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));

        purchase.Code.ShouldBe("OK");
        host.Store.Peek(ReceiptDocument.KeyFor(purchase.ReceiptId))!["outboxPending"]!.GetValue<bool>().ShouldBeTrue();
        (await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_consumer_for_the_reserved_kind_fails_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IOutboxConsumer>(new NoopConsumer(AnalyticsOutbox.Kind)));
        });

        ex.Message.ShouldContain("two consumers handle kind 'analytics'");
    }

    [Fact]
    public async Task A_consumer_for_the_reserved_kind_is_reported_even_when_the_analytics_settings_are_invalid()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(
                settings: s => s["GameServer:Analytics:QueueCapacity"] = "5",
                services: s => s.AddSingleton<IOutboxConsumer>(new NoopConsumer(AnalyticsOutbox.Kind)));
        });

        ex.Violations.ShouldContain(v => v.Contains("two consumers handle kind 'analytics'", StringComparison.Ordinal));
        ex.Violations.Count(v => v.Contains("GameServer:Analytics:QueueCapacity is 5", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_projection_for_the_reserved_kind_fails_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(modules: b => TestHost.DefaultModules(b.AddModule(new ProjectionModule())));
        });

        ex.Message.ShouldContain("two consumers handle kind 'analytics'");
    }

    public sealed record SeamCommand(long By);

    public sealed record SeamResult(long Level);

    public sealed record SeamFields(long Level, string? Missing, string Label);

    /// <summary>A game command emitting a custom at-least-once event through the Core helper only.</summary>
    public sealed class SeamHandler : ICommandHandler<SeamCommand, SeamResult>
    {
        public const string Operation = "seam.emit";

        public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [CounterAggregates.Counter]);

        public ValueTask<CommandOutcome<SeamResult>> HandleAsync(SeamCommand request, ICommandContext context)
        {
            context.AddAnalyticsEvent(new AnalyticsEventType("test_event", 1, "eventTime"), new SeamFields(request.By, null, "gold"));
            return ValueTask.FromResult(CommandOutcome<SeamResult>.Ok(new SeamResult(request.By)));
        }
    }

    private sealed class SeamModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "seam",
            DependsOn = [CounterModuleDefinition.ModuleName],
            Commands = [Command.Define<SeamCommand, SeamResult>(SeamHandler.Descriptor).HandledBy<SeamHandler>()],
            ErrorCodes = new ErrorCodeSet(21000, 21099),
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class ProjectionModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "seam_projection",
            OutboxProjections = [new OutboxProjection(AnalyticsOutbox.Kind, "seam_analytics")],
            Collections = [new CollectionSpec("seam_analytics", Deletion: DeletionTreatment.Anonymize)],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class NoopConsumer(string kind) : IOutboxConsumer
    {
        public string Kind { get; } = kind;

        public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
