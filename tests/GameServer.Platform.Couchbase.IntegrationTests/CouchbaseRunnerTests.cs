using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// The command runner's idempotency contract on real Couchbase transactions (ACC-04, ACC-05, ACC-07, ACC-44),
/// added after the 001 review: the in-memory suite alone cannot prove the SDK behaves as assumed.
/// </summary>
public sealed class CouchbaseRunnerTests(CouchbaseFixture fixture) : IClassFixture<CouchbaseFixture>
{
    private RunnerHarness Harness()
    {
        fixture.RequireCouchbase();
        return new RunnerHarness(o => o.GameId = fixture.GameServer.GameId, fixture.Store);
    }

    private static string NewPlayer() => "p" + Guid.NewGuid().ToString("N")[..12];

    private async Task<long> CounterValueAsync(string playerId)
    {
        var document = await fixture.Store.GetAsync(new DocumentKey("pa_counter", playerId), CancellationToken.None);
        return document?.Content["state"]?["value"]?.GetValue<long>() ?? 0;
    }

    [Fact]
    public async Task Commit_then_replay_has_one_effect()
    {
        using var h = Harness();
        var player = NewPlayer();
        var requestId = Guid.NewGuid().ToString("D");

        var first = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), player, requestId);
        var replay = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), player, requestId);

        first.Code.ShouldBe(CoreErrorCodes.Ok);
        replay.Replayed.ShouldBeTrue();
        replay.ReceiptId.ShouldBe(first.ReceiptId);
        (await CounterValueAsync(player)).ShouldBe(5);
    }

    [Fact]
    public async Task Different_body_is_rejected()
    {
        using var h = Harness();
        var player = NewPlayer();
        var requestId = Guid.NewGuid().ToString("D");
        await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), player, requestId);

        var ex = await Should.ThrowAsync<GameServerException>(() => h.RunAsync(IncrementHandler.Operation, new IncrementCommand(6, null), player, requestId));
        ex.Code.ShouldBe(CoreErrorCodes.IdempotencyPayloadMismatch);
    }

    [Fact]
    public async Task Business_rejection_commits_nothing()
    {
        using var h = Harness();
        var player = NewPlayer();

        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(50, null), player);

        execution.Code.ShouldBe(CounterErrors.LimitExceeded);
        (await fixture.Store.GetAsync(new DocumentKey("pa_counter", player), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Racing_instances_on_one_requestId_have_one_effect()
    {
        using var h = Harness();
        var player = NewPlayer();
        var requestId = Guid.NewGuid().ToString("D");
        var runners = Enumerable.Range(0, 4).Select(_ => h.CreateRunner()).ToArray();

        var results = await Task.WhenAll(runners.Select(r => Task.Run(() =>
            h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), player, requestId, runner: r))));

        results.Select(r => r.ReceiptId).Distinct().Count().ShouldBe(1);
        results.Count(r => !r.Replayed).ShouldBe(1);
        (await CounterValueAsync(player)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_commands_of_one_player_lose_no_update()
    {
        using var h = Harness();
        var player = NewPlayer();
        var runners = Enumerable.Range(0, 4).Select(_ => h.CreateRunner()).ToArray();

        await Task.WhenAll(runners.Select(r => Task.Run(() => h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), player, runner: r))));

        (await CounterValueAsync(player)).ShouldBe(4);
    }

    [Fact]
    public async Task Claim_marker_is_insert_only_and_rolled_back_on_rejection()
    {
        using var h = Harness();
        var player = NewPlayer();
        var key = "claim-" + Guid.NewGuid().ToString("N");

        (await h.RunAsync("probe.claim", new ClaimRequest(key, true), player)).Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        (await fixture.Store.GetAsync(new DocumentKey("probe_claims", key), CancellationToken.None)).ShouldBeNull();

        (await h.RunAsync("probe.claim", new ClaimRequest(key, false), player)).Code.ShouldBe(CoreErrorCodes.Ok);
        (await h.RunAsync("probe.claim", new ClaimRequest(key, false), player)).Code.ShouldBe(ProbeErrors.AlreadyClaimed);
    }

    [Fact]
    public async Task Racing_claims_of_two_players_have_one_winner()
    {
        // ACC-03: identity.link claims the mapping like this; one transaction commits, the other sees the claim.
        using var h = Harness();
        var runners = new[] { h.CreateRunner(), h.CreateRunner() };
        for (var round = 0; round < 5; round++)
        {
            var key = "claim-" + Guid.NewGuid().ToString("N");
            var players = new[] { NewPlayer(), NewPlayer() };

            var results = await Task.WhenAll(players.Select((p, i) => Task.Run(() =>
                h.RunAsync("probe.claim", new ClaimRequest(key, false), p, runner: runners[i]))));

            results.Count(r => r.Code == CoreErrorCodes.Ok).ShouldBe(1);
            results.Count(r => r.Code == ProbeErrors.AlreadyClaimed).ShouldBe(1);
            var winner = players[Array.FindIndex(results, r => r.Code == CoreErrorCodes.Ok)];
            var claim = await fixture.Store.GetAsync(new DocumentKey("probe_claims", key), CancellationToken.None);
            claim!.Content["playerId"]!.GetValue<string>().ShouldBe(winner);
        }
    }

    [Fact]
    public async Task Claim_store_and_command_claim_on_one_key_have_one_winner()
    {
        // A first provider login (ClaimStore) racing identity.link (command claim) on the same mapping key (IDN-01).
        using var h = Harness();
        var claims = new ClaimStore(fixture.Store, new GameServerOptions());
        for (var round = 0; round < 5; round++)
        {
            var key = "claim-" + Guid.NewGuid().ToString("N");
            var player = NewPlayer();

            var insert = Task.Run(() => claims.InsertOrGetAsync(
                new DocumentKey("probe_claims", key),
                new System.Text.Json.Nodes.JsonObject { ["playerId"] = "login-player" },
                CancellationToken.None));
            var command = Task.Run(() => h.RunAsync("probe.claim", new ClaimRequest(key, false), player));
            await Task.WhenAll(insert, command);

            var (stored, inserted) = await insert;
            var commandWon = (await command).Code == CoreErrorCodes.Ok;
            (inserted ^ commandWon).ShouldBeTrue();
            var owner = (await fixture.Store.GetAsync(new DocumentKey("probe_claims", key), CancellationToken.None))!.Content["playerId"]!.GetValue<string>();
            owner.ShouldBe(inserted ? "login-player" : player);
            stored["playerId"]!.GetValue<string>().ShouldBe(owner);
        }
    }

    [Fact]
    public async Task Outbox_is_relayed_and_receipt_marked()
    {
        using var h = Harness();
        var player = NewPlayer();

        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(2, "x"), player);

        var receipt = await fixture.Store.GetAsync(ReceiptDocument.KeyFor(execution.ReceiptId!), CancellationToken.None);
        receipt!.Content["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
        (await fixture.Store.GetAsync(new DocumentKey("counter_log", $"counter_log::{execution.ReceiptId}:0"), CancellationToken.None)).ShouldNotBeNull();
    }

    /// <summary>Slice 016 T053 (FR-031, VERIFY V2 final): a result over its limit is the typed rejection; nothing reaches Couchbase.</summary>
    [Fact]
    public async Task A_result_over_the_limit_is_command_output_too_large_and_writes_nothing()
    {
        using var h = Harness();
        var player = NewPlayer();

        var refused = await h.RunAsync("probe.bigresult", 513, player, encode: _ => new byte[513]);
        var exact = await h.RunAsync("probe.bigresult", 512, NewPlayer(), encode: _ => new byte[512]);

        refused.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        refused.Details["part"].ShouldBe("result");
        refused.ReceiptId.ShouldBeNull();
        (await fixture.Store.GetAsync(new DocumentKey("pa_probe", player), CancellationToken.None)).ShouldBeNull();
        exact.Code.ShouldBe(CoreErrorCodes.Ok);
        (await fixture.Store.GetAsync(ReceiptDocument.KeyFor(exact.ReceiptId!), CancellationToken.None)).ShouldNotBeNull();
    }

    /// <summary>Slice 016 T053: a receipt one byte over its limit is refused with nothing written; exactly at the limit commits.</summary>
    [Fact]
    public async Task A_receipt_over_the_limit_is_command_output_too_large_and_one_at_the_limit_commits()
    {
        using var h = Harness();
        const int Probe = 5000;
        var probe = await h.RunAsync("probe.outboxflood", Probe, NewPlayer());
        probe.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        var atLimit = Probe - (int.Parse(probe.Details["bytes"], System.Globalization.CultureInfo.InvariantCulture) - h.Options.Commands.ReceiptMaxBytes);
        var over = NewPlayer();
        var exact = NewPlayer();

        var refused = await h.RunAsync("probe.outboxflood", atLimit + 1, over);
        var committed = await h.RunAsync("probe.outboxflood", atLimit, exact);

        refused.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        refused.Details["bytes"].ShouldBe((h.Options.Commands.ReceiptMaxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await fixture.Store.GetAsync(new DocumentKey("pa_probe", over), CancellationToken.None)).ShouldBeNull();
        committed.Code.ShouldBe(CoreErrorCodes.Ok);
        (await fixture.Store.GetAsync(new DocumentKey("pa_probe", exact), CancellationToken.None)).ShouldNotBeNull();
        (await fixture.Store.GetAsync(ReceiptDocument.KeyFor(committed.ReceiptId!), CancellationToken.None)).ShouldNotBeNull();
    }

    /// <summary>Slice 016 T053 (FG-029): a touched aggregate written by a newer schema is the typed envelope code; nothing is written.</summary>
    [Fact]
    public async Task A_broken_aggregate_in_touches_is_the_typed_code_and_writes_nothing()
    {
        using var h = Harness();
        var player = NewPlayer();
        var key = new DocumentKey("pa_counter", player);
        var broken = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = h.Registry.GetAggregate(CounterAggregates.Counter).SchemaVersion + 1,
            ["rev"] = 3,
            ["createdAtUtc"] = "2026-01-01T00:00:00.000Z",
            ["state"] = new System.Text.Json.Nodes.JsonObject { ["value"] = 7 },
        };
        (await fixture.Store.InsertIfAbsentAsync(key, broken, CancellationToken.None)).ShouldBeTrue();

        var ex = await Should.ThrowAsync<GameServerException>(() => h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), player));

        ex.Code.ShouldBe(CoreErrorCodes.AggregateVersionAhead);
        ex.Details!["aggregate"].ShouldBe(CounterAggregates.Counter);
        var stored = await fixture.Store.GetAsync(key, CancellationToken.None);
        stored!.Content["rev"]!.GetValue<long>().ShouldBe(3);
        stored.Content["state"]!["value"]!.GetValue<long>().ShouldBe(7);
    }
}
