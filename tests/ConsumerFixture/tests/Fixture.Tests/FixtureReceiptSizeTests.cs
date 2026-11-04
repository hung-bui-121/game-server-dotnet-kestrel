using System.Globalization;
using System.Text.Json.Nodes;
using Fixture.Game;
using GameServer.Actors;
using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.TestKit;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// The receipt size of a game command checked with the kit (FG-016, FR-034, SC-005; slice 016 T065): <c>CheckCommit(outcome,
/// encode)</c> measures the receipt the runner would store with the runner's own builder, so a command that would be refused with
/// <c>COMMAND_OUTPUT_TOO_LARGE</c> fails in a unit test. Consumer guide §13.4 takes its example from here.
/// </summary>
public sealed class FixtureReceiptSizeTests
{
    private const string RequestId = "6c1f0e9a-2b3d-4e5f-8a9b-0c1d2e3f4a5b";

    private static readonly IGameConfig Rules =
        TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = "{\"maxLength\":40}" }, "fixture-test");

    /// <summary>The endpoint's result mapping (<c>FixtureModule.MapPlayerApi</c>): the receipt stores these bytes.</summary>
    private static byte[] Encode(object result) => new StringValue { Value = ((SaveNoteResult)result).Text }.ToByteArray();

    [Fact]
    public async Task A_save_fits_the_default_limits()
    {
        var context = CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule()).AsPlayer("p_1").WithConfig(Rules);

        var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote(new string('n', 40)), context);

        outcome.IsOk.ShouldBeTrue();
        context.CheckCommit(outcome, Encode).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_kit_measures_the_receipt_the_host_stores_and_reports_a_limit_it_exceeds()
    {
        await using var host = await TestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<FixtureModule>(),
            config: TestGameConfig.FromDirectory(FixtureConfigTests.CommittedConfig()));
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var response = await host.BearerClient(login.SessionToken).PostProtobufAsync(FixtureModule.SaveRoute, new StringValue { Value = "hello" }, RequestId);
        var command = await response.ReadProtobufAsync<CommandResponse>();
        command.Code.ShouldBe("OK");
        var receipt = host.Store.Peek(ReceiptDocument.KeyFor(command.ReceiptId)).ShouldNotBeNull();
        var stored = ReceiptDocument.SizeOf(receipt);

        // Exactly at the limit commits ("greater than" rule); one byte less is the runner's rejection, as the kit's line.
        (await CheckAsync(host, login.PlayerId, receipt, stored)).ShouldBeEmpty();
        (await CheckAsync(host, login.PlayerId, receipt, stored - 1)).ShouldBe(
        [
            $"Command 'fixture.save' receipt would be {stored} bytes; ReceiptMaxBytes is {stored - 1}: the runner rejects the command with " +
            "COMMAND_OUTPUT_TOO_LARGE (part receipt) and nothing commits. Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes.",
        ]);
    }

    /// <summary>
    /// The save of <paramref name="playerId"/> in the double with the inputs of the host's receipt: actor, request and receipt id,
    /// config, time and the note the welcome initializer created at rev 1. The kit applies the limit as given (the startup ranges
    /// are checked by the host, not by the double).
    /// </summary>
    private static async Task<IReadOnlyList<string>> CheckAsync(TestHost host, string playerId, JsonObject receipt, int receiptMaxBytes)
    {
        var context = CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule())
            .WithActor(new ActorContext(playerId, ActorType.Player, playerId, Text(receipt, "gameId"), Text(receipt, "environmentId"), new HashSet<string>(), Text(receipt, "correlationId")))
            .WithRequestId(Text(receipt, "requestId"))
            .WithReceiptId(Text(receipt, "receiptId"))
            .WithConfig(host.Services.GetRequiredService<IGameConfig>())
            .At(DateTimeOffset.Parse(Text(receipt, "committedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
            .WithCommandSettings(new CommandSettings { ReceiptMaxBytes = receiptMaxBytes })
            .Seed(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = FixtureWelcomeInitializer.WelcomeText });

        var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("hello"), context);

        return context.CheckCommit(outcome, Encode);
    }

    private static string Text(JsonObject receipt, string name) => receipt[name]!.GetValue<string>();
}
