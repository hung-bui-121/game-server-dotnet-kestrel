using System.Text;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static GameServer.Platform.Web.Tests.KitViolationHandler;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T031 (FR-023..FR-025, US2-5, US2-6, SC-003): every access rule of the runner's context and the derived ids behave
/// identically through <see cref="ICommandRunner"/> on a kit <see cref="TestHost"/> and through <see cref="CommandContextDouble"/>:
/// same exception type and innermost <see cref="InvalidOperationException"/> message, same return values, same ids.
/// </summary>
public sealed class CommandContextConformanceTests
{
    private const string Purposes = "battle,loot,shop,daily,boss";

    private static readonly string[] RequestIds =
    [
        "00000000-0000-4000-8000-000000000001",
        "11111111-1111-4111-8111-111111111111",
        "5d1f6b2a-3c4e-4f60-9a7b-8c9d0e1f2a3b",
        "a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d",
        "ffffffff-ffff-4fff-bfff-ffffffffffff",
    ];

    /// <summary>The cases that must throw (a pass where both sides return would compare nothing).</summary>
    private static readonly HashSet<string> Violations =
        [WriteOutsideTouches, ReadOutsideDeclared, WrongStateType, ClaimOverLimit, ClaimInReceipts, ClaimInAggregate];

    public static TheoryData<string> RuleCases() =>
        new(WriteOutsideTouches, ReadOutsideDeclared, WrongStateType, ClaimOverLimit, ClaimInReceipts, ClaimInAggregate, DuplicateClaim, PreexistingClaim, SetThenGet);

    [Theory]
    [MemberData(nameof(RuleCases))]
    public async Task A_rule_case_behaves_identically_through_the_runner_and_the_double(string ruleCase)
    {
        await using var host = await StartAsync();
        var playerId = NewPlayerId();
        var requestId = Guid.NewGuid().ToString("D");
        if (ruleCase == PreexistingClaim)
        {
            var seeded = await RunAsync(host, KitProbeModule.SaveOperation, new KitSave("seed", ClaimKey: PreexistingKey), NewPlayerId(), Guid.NewGuid().ToString("D"));
            seeded.ShouldBe(Observed.Returned("seed"));
        }

        var throughRunner = await RunAsync(host, KitProbeModule.ViolateOperation, new KitViolation(ruleCase), playerId, requestId);
        var context = CommandContextDouble.Create(KitProbeModule.ViolateOperation, new IdentityModule(), new KitProbeModule()).AsPlayer(playerId).WithRequestId(requestId);
        if (ruleCase == PreexistingClaim)
        {
            context.SeedClaim(KitProbeModule.ClaimCollection, PreexistingKey);
        }

        var throughDouble = await HandleAsync(context, new KitViolation(ruleCase));

        throughDouble.ShouldBe(throughRunner);
        throughRunner.Threw.ShouldBe(Violations.Contains(ruleCase), $"{ruleCase}: {throughRunner}");
    }

    [Fact]
    public async Task Derived_ids_and_seeds_are_the_runners_for_the_same_receipt()
    {
        await using var host = await StartAsync();
        var playerId = NewPlayerId();
        var samples = 0;
        foreach (var requestId in RequestIds)
        {
            var request = new KitViolation(Ids, Purposes);
            var throughRunner = await RunAsync(host, KitProbeModule.ViolateOperation, request, playerId, requestId);
            var context = CommandContextDouble.Create(KitProbeModule.ViolateOperation, new IdentityModule(), new KitProbeModule())
                .AsPlayer(playerId)
                .WithRequestId(requestId);

            var throughDouble = await HandleAsync(context, request);
            var again = await HandleAsync(
                CommandContextDouble.Create(KitProbeModule.ViolateOperation, new KitProbeModule()).WithReceiptId(context.ReceiptId),
                request);

            throughRunner.Threw.ShouldBeFalse(throughRunner.Value);
            throughDouble.ShouldBe(throughRunner);
            again.ShouldBe(throughRunner);
            samples += throughRunner.Value.Split(';').Length;
        }

        samples.ShouldBe(25);
    }

    /// <summary>
    /// Review m6: the double's commit check finds the runner's HardBytes boundary of a written aggregate (the largest note that
    /// commits, the smallest that the runner rejects with AGGREGATE_TOO_LARGE) and the runner agrees on both sides.
    /// </summary>
    [Fact]
    public async Task The_hard_bytes_boundary_of_the_commit_check_is_the_runners()
    {
        await using var host = await StartSizeAsync();
        var playerId = NewPlayerId();
        var fits = Enumerable.Range(1, KitSizeModule.HardBytes)
            .Last(length => SizeCheck(playerId, new KitSize(NoteLength: length)).Result.Count == 0);
        var (_, tooLarge) = SizeCheck(playerId, new KitSize(NoteLength: fits + 1));

        fits.ShouldBeGreaterThan(KitSizeModule.HardBytes / 2, "the boundary must come from the budget, not from an earlier failure");
        tooLarge.ShouldHaveSingleItem().ShouldStartWith(
            $"Command 'kit.size' would store aggregate 'kit_sized' as {KitSizeModule.HardBytes + 1} bytes; its HardBytes is {KitSizeModule.HardBytes}: " +
            "the runner rejects the command with AGGREGATE_TOO_LARGE and nothing commits.");
        (await RunAsync(host, KitSizeModule.Operation, new KitSize(NoteLength: fits), playerId, Guid.NewGuid().ToString("D")))
            .ShouldBe(Observed.Returned("r"));
        (await RunAsync(host, KitSizeModule.Operation, new KitSize(NoteLength: fits + 1), NewPlayerIdOfLength(playerId.Length), Guid.NewGuid().ToString("D")))
            .ShouldBe(Observed.Returned("AGGREGATE_TOO_LARGE"));
    }

    /// <summary>Review m6: the resultCompact limit, with the same encoder on both sides, refuses the same lengths with the same message.</summary>
    [Theory]
    [InlineData(512)]
    [InlineData(513)]
    public async Task The_result_size_check_is_the_runners(int resultLength)
    {
        await using var host = await StartSizeAsync();
        var playerId = NewPlayerId();
        var request = new KitSize(ResultLength: resultLength);

        var throughRunner = await RunAsync(host, KitSizeModule.Operation, request, playerId, Guid.NewGuid().ToString("D"));
        var context = SizeDouble(playerId);
        var violations = context.CheckCommit(await new KitSizeHandler().HandleAsync(request, context), result => Encoding.UTF8.GetBytes((string)result));

        if (resultLength <= 512)
        {
            throughRunner.Threw.ShouldBeFalse(throughRunner.Value);
            violations.ShouldBeEmpty();
        }
        else
        {
            throughRunner.ShouldBe(new Observed(true, nameof(InvalidOperationException), "Result of 'kit.size' is 513 bytes; resultCompact max is 512 (CMD-03)."));
            violations.ShouldBe([throughRunner.Value]);
        }
    }

    /// <summary>Review m7: a set of the loaded state (or a change set back) is no write for the runner and no entry in Writes.</summary>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(5, true, false)]
    [InlineData(5, false, true)]
    public async Task A_no_op_set_writes_nothing_through_the_runner_and_the_double(int noteLength, bool setBack, bool written)
    {
        await using var host = await StartSizeAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        var playerId = NewPlayerId();
        var request = new KitSize(noteLength, setBack);
        var actor = new ActorContext(playerId, ActorType.Player, playerId, "test", "test", new HashSet<string>(StringComparer.Ordinal), "corr");

        var execution = await runner.ExecuteAsync(
            new CommandInvocation(KitSizeModule.Operation, actor, Guid.NewGuid().ToString("D"), "body", request, result => Encoding.UTF8.GetBytes((string)result)),
            CancellationToken.None);
        var context = SizeDouble(playerId);
        await new KitSizeHandler().HandleAsync(request, context);

        execution.Code.Name.ShouldBe("OK");
        var collection = host.Services.GetRequiredService<ModuleRegistry>().GetAggregate(KitSizeModule.Sized).Collection;
        host.Store.Count(collection).ShouldBe(written ? 1 : 0, "the runner stores the aggregate only when its state changed");
        context.Writes.Select(w => w.AggregateId).ShouldBe(written ? [KitSizeModule.Sized] : []);
        context.CheckCommit(CommandOutcome<string>.Ok("r")).ShouldBeEmpty();
    }

    private static Task<TestHost> StartSizeAsync() =>
        TestHost.StartAsync(modules: b => b.AddModule<IdentityModule>().AddModule(new KitSizeModule()));

    private static CommandContextDouble SizeDouble(string playerId) =>
        CommandContextDouble.Create(KitSizeModule.Operation, new KitSizeModule()).AsPlayer(playerId);

    private static (CommandContextDouble Context, IReadOnlyList<string> Result) SizeCheck(string playerId, KitSize request)
    {
        var context = SizeDouble(playerId);
        var outcome = new KitSizeHandler().HandleAsync(request, context).AsTask().GetAwaiter().GetResult();
        return (context, context.CheckCommit(outcome));
    }

    /// <summary>Another player id of the same length: the envelope (and so the boundary) depends on the id's length only.</summary>
    private static string NewPlayerIdOfLength(int length)
    {
        var id = NewPlayerId();
        id.Length.ShouldBe(length);
        return id;
    }

    private static Task<TestHost> StartAsync() =>
        TestHost.StartAsync(modules: b => b.AddModule<IdentityModule>().AddModule(new KitProbeModule()));

    private static string NewPlayerId() => "p_" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<Observed> RunAsync(TestHost host, string operation, object request, string playerId, string requestId)
    {
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        var actor = new ActorContext(playerId, ActorType.Player, playerId, "test", "test", new HashSet<string>(StringComparer.Ordinal), requestId);
        try
        {
            var execution = await runner.ExecuteAsync(
                new CommandInvocation(operation, actor, requestId, "body-" + requestId, request, result => Encoding.UTF8.GetBytes((string)result)),
                CancellationToken.None);
            return execution.Code.Name == "OK" ? Observed.Returned(Encoding.UTF8.GetString(execution.Result)) : Observed.Returned(execution.Code.Name);
        }
        catch (Exception ex)
        {
            return Observed.Of(ex);
        }
    }

    private static async Task<Observed> HandleAsync(CommandContextDouble context, KitViolation request)
    {
        try
        {
            var outcome = await new KitViolationHandler().HandleAsync(request, context);
            return Observed.Returned(outcome.Result!);
        }
        catch (Exception ex)
        {
            return Observed.Of(ex);
        }
    }

    /// <summary>A return value, or the innermost <see cref="InvalidOperationException"/> (type and message) of a failure.</summary>
    private sealed record Observed(bool Threw, string Type, string Value)
    {
        public static Observed Returned(string value) => new(false, string.Empty, value);

        public static Observed Of(Exception ex)
        {
            Exception innermost = ex;
            for (var current = ex; current is not null; current = current.InnerException)
            {
                if (current is InvalidOperationException)
                {
                    innermost = current;
                }
            }

            return new(true, innermost.GetType().Name, innermost.Message);
        }
    }
}
