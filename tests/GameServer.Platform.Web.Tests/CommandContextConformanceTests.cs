using System.Text;
using GameServer.Actors;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
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
