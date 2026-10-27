using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Gate;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>StoreGateEvaluator cache semantics (PERF-08, GATE-01): TTL, fail-open, invalidation, clock steps.</summary>
public sealed class GateEvaluatorTests
{
    private sealed class ScriptedStore : IKeyValueStore
    {
        public GateDocument Gate { get; set; } = new();

        public bool Fail { get; set; }

        public int Reads { get; private set; }

        public Func<Task>? DuringRead { get; set; }

        public async Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
        {
            Reads++;
            if (DuringRead is { } hook)
            {
                await hook();
            }

            if (Fail)
            {
                throw new TimeoutException("store down");
            }

            return new StoredDocument(key, Gate.ToJson(), null);
        }

        public Task<bool> InsertAsync(DocumentKey key, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReplaceAsync(StoredDocument existing, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(DocumentKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly GateDocument Down = new() { Maintenance = new MaintenanceState { Enabled = true, Message = "down" } };

    private static (StoreGateEvaluator Evaluator, ScriptedStore Store, FakeClock Clock) Create()
    {
        var store = new ScriptedStore();
        var clock = new FakeClock();
        return (new StoreGateEvaluator(store, clock, new GameServerOptions(), NullLogger<StoreGateEvaluator>.Instance), store, clock);
    }

    private static Task<GateResult> EvaluateAsync(StoreGateEvaluator evaluator) => evaluator.EvaluateAsync(new DefaultHttpContext()).AsTask();

    [Fact]
    public async Task A_change_shows_within_the_ttl_and_a_failed_read_keeps_the_last_state()
    {
        var (evaluator, store, clock) = Create();
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Allow);

        store.Gate = Down;
        clock.Advance(TimeSpan.FromSeconds(9));
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Allow);
        clock.Advance(TimeSpan.FromSeconds(1));
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Maintenance);

        store.Fail = true;
        clock.Advance(TimeSpan.FromSeconds(11));
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Maintenance);
    }

    [Fact]
    public async Task A_failed_first_read_opens_the_gate_only_briefly()
    {
        var (evaluator, store, clock) = Create();
        store.Fail = true;
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Allow);

        store.Fail = false;
        store.Gate = Down;
        clock.Advance(TimeSpan.FromSeconds(1));
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Maintenance);
    }

    [Fact]
    public async Task An_invalidation_during_a_refresh_is_not_lost()
    {
        var (evaluator, store, _) = Create();
        store.DuringRead = () =>
        {
            // The GM change commits while this instance is reading the old document.
            evaluator.Invalidate();
            return Task.CompletedTask;
        };
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Allow);

        store.DuringRead = null;
        store.Gate = Down;
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Maintenance);
    }

    [Fact]
    public async Task A_clock_stepping_back_does_not_freeze_the_gate()
    {
        var (evaluator, store, clock) = Create();
        await EvaluateAsync(evaluator);
        store.Gate = Down;
        clock.Advance(TimeSpan.FromHours(-1));
        (await EvaluateAsync(evaluator)).Decision.ShouldBe(GateDecision.Maintenance);
    }

    [Theory]
    [InlineData("1.2", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1..2", false)]
    [InlineData("1.-2", false)]
    [InlineData("1.2 ", false)]
    [InlineData("1.9999999999", false)]
    public void Client_versions_parse_strictly(string value, bool valid) => ClientVersion.TryParse(value, out _).ShouldBe(valid);
}
