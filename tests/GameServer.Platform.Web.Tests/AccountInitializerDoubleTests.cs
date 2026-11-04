using System.Net;
using GameServer.Actors;
using GameServer.Config;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T030 (FR-017, FR-034, US2-8, research R14): an account initializer runs host-free in the kit's double, behind the
/// account writer's view, and <c>CheckAccountCreation</c> returns the writer's own rule lines.
/// </summary>
public sealed class AccountInitializerDoubleTests
{
    private static readonly ResourceCatalog Resources = new([new ResourceDefinition("gold", ResourceKind.Currency, 1_000_000, "Gold")]);

    [Fact]
    public async Task The_initializer_runs_host_free_with_the_creation_actor_and_its_writes()
    {
        var ctx = Double();

        (await new StartingStateInitializer(StartingStateBehaviour.Grant(100), Resources).InitializeAsync(ctx)).ShouldBeNull();

        ctx.Descriptor.Operation.ShouldBe(AccountCreation.Operation);
        ctx.Actor.ActorType.ShouldBe(ActorType.System);
        ctx.Actor.ActorId.ShouldBe(ctx.Actor.PlayerId);
        ctx.ReceiptId.ShouldBe(ReceiptIds.Compute(ctx.Actor.ActorId, ctx.Actor.PlayerId!, AccountCreation.Operation, "00000000-0000-4000-8000-000000000001"));
        ctx.Written<WalletState>(EconomyAggregates.Wallet)!.Balances["gold"].ShouldBe(100);
        ctx.Written<StarterState>(StartingStateAggregates.Starter)!.Value.ShouldBe(100);
        ctx.Outbox.Select(e => e.Kind).ShouldBe([EconomyOperations.LedgerKind]);
        ctx.CheckAccountCreation().ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_initializer_or_a_missing_aggregate_module_is_an_argument_error()
    {
        Should.Throw<ArgumentException>(() => CommandContextDouble.ForAccountInitializer("test.nope", new StartingStateModule(), new EconomyModule()))
            .Message.ShouldStartWith("Account initializer 'test.nope' is not registered by the given modules. Registered initializers: test.starting_state.");
        Should.Throw<ArgumentException>(() => CommandContextDouble.ForAccountInitializer(StartingStateModule.InitializerName, new StartingStateModule()))
            .Message.ShouldStartWith("Account initializer 'test.starting_state' declares aggregate 'wallet' in Touches, but no given module registers it.");
    }

    [Fact]
    public async Task An_undeclared_write_throws_the_writers_message()
    {
        var ctx = Double();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new StartingStateInitializer(StartingStateBehaviour.WriteUndeclared(), Resources).InitializeAsync(ctx));

        ex.Message.ShouldBe("Command 'identity.create_account (initializer test.starting_state)' wrote aggregate 'inventory' which is not declared in Touches (DAT-09).");
    }

    [Fact]
    public void A_command_double_and_an_initializer_double_refuse_each_others_check()
    {
        Should.Throw<InvalidOperationException>(() => Double().CheckCommit(GameServer.Commands.CommandOutcome<int>.Ok(1)));
        Should.Throw<InvalidOperationException>(() => CommandContextDouble.Create("economy.adjust", new EconomyModule()).CheckAccountCreation());
    }

    [Theory]
    [InlineData(StartingStateMode.AddAudit)]
    [InlineData(StartingStateMode.OversizeAggregate)]
    [InlineData(StartingStateMode.FloodOutbox)]
    public async Task CheckAccountCreation_returns_the_rule_line_the_account_writer_logs(StartingStateMode mode)
    {
        var behaviour = Behaviour(mode);
        await using var host = await SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()),
            services: s => s.AddSingleton(behaviour));
        var localId = IdentityHttp.NewLocalId();
        var refused = await host.Client().PostProtobufAsync(
            IdentityModule.LoginRoute,
            new GuestLoginRequest { LocalId = localId, Secret = ByteString.CopyFrom(IdentityHttp.NewSecret()) },
            ProtobufHttp.NewRequestId());
        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var mapping = host.Store.Peek(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId))).ShouldNotBeNull();
        var playerId = mapping["playerId"]!.GetValue<string>();
        var logged = host.LogEntries.Single(e => e.Category == AccountWriter.LogCategory && e.Level == LogLevel.Error).Message;

        // Same inputs as the host: the player as System actor, the host's ids, config and time; ids of the same length.
        var ctx = Double()
            .WithActor(new ActorContext(playerId, ActorType.System, playerId, "test", "test", new HashSet<string>(), ProtobufHttp.NewRequestId()))
            .WithConfig(host.Services.GetRequiredService<IGameConfig>())
            .At(host.Services.GetRequiredService<IClock>().UtcNow);
        (await new StartingStateInitializer(Behaviour(mode), Resources).InitializeAsync(ctx)).ShouldBeNull();

        var line = ctx.CheckAccountCreation().ShouldHaveSingleItem();
        logged.ShouldBe($"Account creation of player {playerId} failed: {line} Nothing was created; the next login retries.");
    }

    private static StartingStateBehaviour Behaviour(StartingStateMode mode) => mode switch
    {
        StartingStateMode.AddAudit => StartingStateBehaviour.AddAudit(),
        StartingStateMode.OversizeAggregate => StartingStateBehaviour.OversizeAggregate(),
        _ => StartingStateBehaviour.FloodOutbox(40),
    };

    private static CommandContextDouble Double() =>
        CommandContextDouble.ForAccountInitializer(StartingStateModule.InitializerName, new StartingStateModule(), new EconomyModule());
}
