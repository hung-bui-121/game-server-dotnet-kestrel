using System.Text.Json.Nodes;
using GameServer.Accounts;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T025/T026 (FR-010..FR-016, US2-1..US2-4, US2-6, research R1, R2, data-model §1.2, §1.3): a first login runs the
/// account initializers over one fresh context per attempt, checks every commit rule before staging and writes one receipt when
/// the initializers had effects; a failing initializer leaves nothing and is reported once.
/// </summary>
public sealed class AccountWriterCreationTests
{
    private const string PlayerId = "p_seed";
    private const string MappingKey = "guest:seed";
    private const string Correlation = "corr-seed";

    private static readonly DocumentKey Anchor = new(IdentityCollections.Identities, MappingKey);

    public enum Failure
    {
        Rejected,
        Threw,
        UndeclaredWrite,
        UndeclaredRead,
        Audit,
        OversizeAggregate,
        ClaimOverOwnLimit,
        ReceiptTooLarge,
    }

    [Fact]
    public async Task Initializers_run_in_order_over_one_shared_context_with_their_di_services()
    {
        using var f = new Fixture();
        f.Script.Steps[SeedModules.First] = ctx =>
        {
            SeedModules.Add(ctx, SeedAggregates.A, 1);
            return Ok();
        };
        f.Script.Steps[SeedModules.Second] = ctx =>
        {
            f.Script.Trace.Enqueue($"second saw {ctx.Get<SeedState>(SeedAggregates.A).Value}");
            SeedModules.Add(ctx, SeedAggregates.A, 10);
            return Ok();
        };

        var result = await f.LoginAsync();

        result.Created.ShouldBeTrue();
        f.Script.Trace.ShouldBe(["second saw 1"]);
        f.Script.Dependencies.Single().ShouldBeOfType<SeedDependency>();
        f.State(SeedAggregates.A)!["state"]!["value"]!.GetValue<long>().ShouldBe(11);
        f.State(SeedAggregates.A)!["rev"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task The_creation_receipt_carries_the_fields_of_the_data_model()
    {
        using var f = new Fixture();
        f.Script.Steps[SeedModules.First] = async ctx =>
        {
            SeedModules.Add(ctx, SeedAggregates.A, 5);
            (await ctx.TryClaimAsync(SeedModules.ClaimCollection, PlayerId)).ShouldBeTrue();
            ctx.AddOutbox(SeedModules.OutboxKind, new { n = 1 });
            return null;
        };

        var result = await f.LoginAsync();

        result.Created.ShouldBeTrue();
        result.CreationReceiptId.ShouldNotBeNull();
        var receipt = ReceiptDocument.FromJson(f.H.Store.Peek(ReceiptDocument.KeyFor(result.CreationReceiptId))!);
        Guid.TryParseExact(receipt.RequestId, "D", out _).ShouldBeTrue();
        receipt.RequestId.ShouldBe(receipt.RequestId.ToLowerInvariant());
        receipt.ReceiptId.ShouldBe(ReceiptIds.Compute(PlayerId, PlayerId, AccountCreation.Operation, receipt.RequestId));
        receipt.ActorId.ShouldBe(PlayerId);
        receipt.ActorType.ShouldBe("system");
        receipt.PlayerId.ShouldBe(PlayerId);
        receipt.Operation.ShouldBe("identity.create_account");
        receipt.BodyHash.ShouldBe(ReceiptIds.BodyHash([]));
        receipt.ResultCode.ShouldBe("OK");
        receipt.ResultCompact.ShouldBe(string.Empty);
        receipt.ConfigVersion.ShouldBe(f.H.Config.ConfigVersion);
        receipt.SchemaVersions.Keys.ShouldBe([SeedAggregates.A, SeedAggregates.B]);
        receipt.Aggregates.ShouldBe([new ReceiptAggregate(SeedAggregates.A, 1)]);
        receipt.ClaimKeys.ShouldBe([$"{SeedModules.ClaimCollection}/{PlayerId}"]);
        receipt.Outbox.Select(e => e.Kind).ShouldBe([SeedModules.OutboxKind]);
        receipt.CorrelationId.ShouldBe(Correlation);
        f.H.Store.Peek(new DocumentKey(SeedModules.ClaimCollection, PlayerId))!["receiptId"]!.GetValue<string>().ShouldBe(receipt.ReceiptId);

        // Every read and every rule before the first staged write; the receipt is staged last.
        f.Store.Operations.ShouldBe([
            "read pa_profile",
            "read identities,pa_seed_a,pa_seed_b",
            "read seed_claims",
            "replace identities",
            "insert pa_profile",
            "insert pa_seed_a",
            "insert seed_claims",
            "insert receipts",
        ]);
        f.Logs(LogLevel.Information).ShouldBe([$"Account {PlayerId} created: 2 initializers, receipt {receipt.ReceiptId}."]);
        f.Metrics.SumTagged("account_creations_total", ("result", "created")).ShouldBe(1);
        f.Metrics.SumTagged("txn_attempts_total", ("operation", AccountCreation.Operation)).ShouldBe(1);
        f.Metrics.Values("receipt_size_bytes").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Initializers_that_change_nothing_write_the_profile_alone_without_a_receipt()
    {
        using var f = new Fixture();

        var result = await f.LoginAsync();

        result.Created.ShouldBeTrue();
        result.CreationReceiptId.ShouldBeNull();
        f.Store.Operations.ShouldBe(["read pa_profile", "read identities,pa_seed_a,pa_seed_b", "replace identities", "insert pa_profile"]);
        f.H.Store.Count(ReceiptDocument.Collection).ShouldBe(0);
        f.Script.Invocations(SeedModules.First).ShouldBe(1);
        f.Script.Invocations(SeedModules.Second).ShouldBe(1);
        f.Logs(LogLevel.Information).ShouldBe([$"Account {PlayerId} created: 2 initializers, receipt none."]);
        f.Metrics.SumTagged("account_creations_total", ("result", "created")).ShouldBe(1);
    }

    [Fact]
    public async Task A_store_rerun_and_a_race_round_keep_the_receipt_id_and_the_drawn_ids()
    {
        using var f = new Fixture();
        f.Script.Steps[SeedModules.First] = ctx =>
        {
            f.Script.Trace.Enqueue($"{ctx.NewId("gift")}|{ctx.NewSeed("roll")}");
            SeedModules.Add(ctx, SeedAggregates.A, 1);
            return Ok();
        };

        // Round 1 loses a faked insert race; round 2 conflicts once and the store re-runs its body.
        f.Store.InsertRacesToFake = 1;
        f.H.Store.Faults.Enqueue(StoreFault.ConflictOnce);

        var result = await f.LoginAsync();

        result.Created.ShouldBeTrue();
        f.Store.BodyRuns.ShouldBe(3);
        f.Script.Invocations(SeedModules.First).ShouldBe(3);
        f.Script.Trace.Distinct().Count().ShouldBe(1);
        f.H.Store.Count(ReceiptDocument.Collection).ShouldBe(1);
        f.State(SeedAggregates.A)!["state"]!["value"]!.GetValue<long>().ShouldBe(1);
        f.Metrics.SumTagged("txn_retries_total", ("operation", AccountCreation.Operation)).ShouldBe(2);
    }

    [Fact]
    public async Task An_existing_profile_never_runs_the_initializers()
    {
        using var f = new Fixture();
        await f.LoginAsync();
        f.Store.Operations.Clear();

        var result = await f.LoginAsync();

        result.Created.ShouldBeFalse();
        result.CreationReceiptId.ShouldBeNull();
        f.Script.Invocations(SeedModules.First).ShouldBe(1);
        f.Store.Operations.ShouldBe(["read pa_profile", "replace pa_profile"]);
    }

    [Theory]
    [InlineData(Failure.Rejected)]
    [InlineData(Failure.Threw)]
    [InlineData(Failure.UndeclaredWrite)]
    [InlineData(Failure.UndeclaredRead)]
    [InlineData(Failure.Audit)]
    [InlineData(Failure.OversizeAggregate)]
    [InlineData(Failure.ClaimOverOwnLimit)]
    [InlineData(Failure.ReceiptTooLarge)]
    public async Task A_failing_initializer_creates_nothing_is_reported_once_and_the_next_login_creates(Failure failure)
    {
        using var f = new Fixture(receiptMaxBytes: failure == Failure.ReceiptTooLarge ? 1112 : null);
        var (initializer, result, line) = Arrange(f, failure);

        var ex = await Should.ThrowAsync<GameServerException>(f.LoginAsync);

        ex.Code.ShouldBe(CoreErrorCodes.Internal);
        ex.InnerException.ShouldBeNull();
        foreach (var collection in new[] { "pa_profile", "pa_seed_a", "pa_seed_b", ReceiptDocument.Collection, SeedModules.ClaimCollection })
        {
            f.H.Store.Count(collection).ShouldBe(0, collection);
        }

        f.Store.Operations.ShouldNotContain(op => op.StartsWith("insert", StringComparison.Ordinal) || op.StartsWith("replace", StringComparison.Ordinal));
        var errors = f.Logs(LogLevel.Error);
        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(line(errors[0]));
        errors[0].ShouldNotContain("secret");
        f.Metrics.SumTagged("account_initializer_failures_total", ("initializer", initializer), ("result", result)).ShouldBe(1);
        f.Metrics.SumTagged("account_creations_total", ("result", "failed")).ShouldBe(1);
        f.Metrics.SumTagged("command_output_too_large_total", ("operation", AccountCreation.Operation), ("part", "receipt"))
            .ShouldBe(failure == Failure.ReceiptTooLarge ? 1 : 0);

        f.Script.Steps.Clear();
        f.Script.Steps[SeedModules.First] = ctx =>
        {
            SeedModules.Add(ctx, SeedAggregates.A, 100);
            return Ok();
        };

        var retried = await f.LoginAsync();

        retried.Created.ShouldBeTrue();
        retried.CreationReceiptId.ShouldNotBeNull();
        f.State(SeedAggregates.A)!["state"]!["value"]!.GetValue<long>().ShouldBe(100);
        f.H.Store.Count(ReceiptDocument.Collection).ShouldBe(1);
    }

    private static (string Initializer, string Result, Func<string, string> Line) Arrange(Fixture f, Failure failure)
    {
        const string Prefix = $"Account creation of player {PlayerId} failed: ";
        const string Suffix = " Nothing was created; the next login retries.";
        const string First = "identity.create_account (initializer seed.first)";
        const string Second = "identity.create_account (initializer seed.second)";
        switch (failure)
        {
            case Failure.Rejected:
                f.Script.Steps[SeedModules.First] = _ => ValueTask.FromResult<ErrorCode?>(SeedErrors.Rejected);
                return (SeedModules.First, "rejected", _ => Prefix + "initializer seed.first (module seed) returned SEED_REJECTED." + Suffix);
            case Failure.Threw:
                f.Script.Steps[SeedModules.First] = _ => throw new InvalidOperationException("secret state content");
                return (SeedModules.First, "threw", _ => Prefix + "initializer seed.first (module seed) threw System.InvalidOperationException." + Suffix);
            case Failure.UndeclaredWrite:
                f.Script.Steps[SeedModules.Second] = ctx =>
                {
                    ctx.Set(SeedAggregates.B, new SeedState { Value = 1 });
                    return Ok();
                };
                return (SeedModules.Second, "rule", _ =>
                    Prefix + $"Command '{Second}' wrote aggregate 'seed_b' which is not declared in Touches (DAT-09)." + Suffix);
            case Failure.UndeclaredRead:
                f.Script.Steps[SeedModules.Second] = ctx =>
                {
                    ctx.Get<SeedState>(SeedAggregates.B);
                    return Ok();
                };
                return (SeedModules.Second, "rule", _ =>
                    Prefix + $"Command '{Second}' accessed aggregate 'seed_b' which is not declared in Touches or Reads (DAT-11)." + Suffix);
            case Failure.Audit:
                f.Script.Steps[SeedModules.First] = ctx =>
                {
                    SeedModules.Add(ctx, SeedAggregates.A, 1);
                    ctx.AddAudit("seed.perm", "seed", "test");
                    return Ok();
                };
                return (SeedModules.First, "rule", _ =>
                    Prefix + $"Command '{First}' (Player) wrote 1 audit entries; expected 0 (SEC-14). " +
                    "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do." + Suffix);
            case Failure.OversizeAggregate:
                f.Script.Steps[SeedModules.First] = ctx =>
                {
                    SeedModules.Add(ctx, SeedAggregates.A, 1, new string('x', SeedAggregates.AHardBytes));
                    return Ok();
                };

                // The measured size is the runner's (CommitRules.CheckAggregateSize); the line keeps it.
                return (SeedModules.First, "rule", actual => Prefix +
                    $"Command '{First}' would store aggregate 'seed_a' as {Between(actual, "' as ", " bytes")} bytes; its HardBytes is 2048: " +
                    "the runner rejects the command with AGGREGATE_TOO_LARGE and nothing commits. Fix: keep the state smaller or split it into another aggregate." +
                    Suffix);
            case Failure.ClaimOverOwnLimit:
                f.Script.Steps[SeedModules.Second] = async ctx =>
                {
                    await ctx.TryClaimAsync(SeedModules.ClaimCollection, "a");
                    await ctx.TryClaimAsync(SeedModules.ClaimCollection, "b");
                    return null;
                };
                return (SeedModules.Second, "rule", _ => Prefix + $"Command '{Second}' exceeded MaxClaimMarkers = 1 (DAT-04)." + Suffix);
            default:
                f.Script.Steps[SeedModules.First] = ctx =>
                {
                    SeedModules.Add(ctx, SeedAggregates.A, 1);
                    for (var i = 0; i < 20; i++)
                    {
                        ctx.AddOutbox(SeedModules.OutboxKind, new { i, text = new string('o', 64) });
                    }

                    return Ok();
                };
                return ("*", "rule", actual => Prefix +
                    $"Command 'identity.create_account (initializer seed.first, seed.second)' receipt would be {Between(actual, "receipt would be ", " bytes")} bytes; " +
                    "ReceiptMaxBytes is 1112: the runner rejects the command with COMMAND_OUTPUT_TOO_LARGE (part receipt) and nothing commits. " +
                    "Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes." + Suffix);
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }

    private static ValueTask<ErrorCode?> Ok() => ValueTask.FromResult<ErrorCode?>(null);

    /// <summary>A harness with the seed module: <c>seed.first</c> (seed_a, seed_b; 1 claim), <c>seed.second</c> (seed_a; 1 claim).</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(int? receiptMaxBytes = null)
        {
            H = new RunnerHarness(
                o =>
                {
                    if (receiptMaxBytes is { } max)
                    {
                        // The smallest receipt limit the settings rule allows (T050): 4 * ceil(64 / 3) + 1024 = 1112.
                        o.Commands.ResultCompactMaxBytes = 64;
                        o.Commands.ReceiptMaxBytes = max;
                    }
                },
                SeedModules.Anchors(),
                SeedModules.Module(
                    Script,
                    SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.B], maxClaimMarkers: 1),
                    SeedModules.SecondOf([SeedAggregates.A], maxClaimMarkers: 1)));
            H.Registry.Validate(H.Options, null).ShouldBeEmpty();
            Metrics = new MetricCollector(H.Metrics);
            Store = new RecordingStore(H.Store);
            Writer = H.CreateAccountWriter(Store);
            H.Store.Seed(Anchor, new JsonObject { ["playerId"] = PlayerId });
        }

        public SeedScript Script { get; } = new();

        public RunnerHarness H { get; }

        public RecordingStore Store { get; }

        public MetricCollector Metrics { get; }

        internal AccountWriter Writer { get; }

        public Task<AccountWriteResult<ProfileState>> LoginAsync() =>
            Writer.WriteLoginAsync(
                new AccountLoginWrite<ProfileState>(PlayerId, p => p with { SessionEpoch = p.SessionEpoch + 1 }, Anchor, Correlation),
                CancellationToken.None);

        public JsonObject? State(string aggregateId) => H.Store.Peek(new DocumentKey("pa_" + aggregateId, PlayerId));

        public IReadOnlyList<string> Logs(LogLevel level) =>
            H.Logs.Lines.Where(l => l.Category == AccountWriter.LogCategory && l.Level == level).Select(l => l.Message).ToArray();

        public void Dispose()
        {
            Metrics.Dispose();
            H.Dispose();
        }
    }
}
