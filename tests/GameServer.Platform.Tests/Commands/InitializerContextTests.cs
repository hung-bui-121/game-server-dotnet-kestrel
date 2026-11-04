using GameServer.Accounts;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T024 (FR-010, FR-013, US2-6, research R3): each account initializer sees the shared creation context through a view
/// limited to its own touches and claim limit; everything else is the shared context's.
/// </summary>
public sealed class InitializerContextTests : IDisposable
{
    private const string PlayerId = "p_view";

    private readonly RunnerHarness _h = new(null, SeedModules.Module(new SeedScript()));

    [Fact]
    public async Task Reads_and_writes_outside_the_initializers_touches_are_refused_with_the_runner_messages()
    {
        await InAttemptAsync((context, tx) =>
        {
            var view = View(context, SeedModules.SecondOf([SeedAggregates.A]));

            Should.Throw<InvalidOperationException>(() => view.Get<SeedState>(SeedAggregates.B)).Message.ShouldBe(
                "Command 'identity.create_account (initializer seed.second)' accessed aggregate 'seed_b' which is not declared in Touches or Reads (DAT-11).");
            Should.Throw<InvalidOperationException>(() => view.Set(SeedAggregates.B, new SeedState())).Message.ShouldBe(
                "Command 'identity.create_account (initializer seed.second)' wrote aggregate 'seed_b' which is not declared in Touches (DAT-09).");
            context.TryGetNewState(SeedAggregates.B, out _).ShouldBeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_claim_over_the_initializers_own_limit_is_refused_while_the_shared_limit_has_room()
    {
        await InAttemptAsync(async (context, tx) =>
        {
            var view = View(context, SeedModules.SecondOf([SeedAggregates.A], maxClaimMarkers: 1));

            (await view.TryClaimAsync(SeedModules.ClaimCollection, "one")).ShouldBeTrue();
            (await Should.ThrowAsync<InvalidOperationException>(async () => await view.TryClaimAsync(SeedModules.ClaimCollection, "two"))).Message
                .ShouldBe("Command 'identity.create_account (initializer seed.second)' exceeded MaxClaimMarkers = 1 (DAT-04).");
            (await context.TryClaimAsync(SeedModules.ClaimCollection, "two")).ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Two_views_over_one_context_see_each_others_writes_in_order()
    {
        await InAttemptAsync((context, tx) =>
        {
            var writers = new Dictionary<string, string>(StringComparer.Ordinal);
            var first = new InitializerContext(context, SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.B]), writers);
            var second = new InitializerContext(context, SeedModules.SecondOf([SeedAggregates.A]), writers);

            SeedModules.Add(first, SeedAggregates.A, 3);
            second.Get<SeedState>(SeedAggregates.A).Value.ShouldBe(3);
            SeedModules.Add(second, SeedAggregates.A, 4);

            first.Get<SeedState>(SeedAggregates.A).Value.ShouldBe(7);
            writers[SeedAggregates.A].ShouldBe(SeedModules.Second);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Every_other_member_is_the_shared_contexts()
    {
        await InAttemptAsync((context, tx) =>
        {
            var view = View(context, SeedModules.FirstOf([SeedAggregates.A]));

            view.Actor.ShouldBeSameAs(context.Actor);
            view.Now.ShouldBe(context.Now);
            view.ConfigVersion.ShouldBe(context.ConfigVersion);
            view.Config.ShouldBeSameAs(context.Config);
            view.AddOutbox(SeedModules.OutboxKind, new { n = 1 });
            context.Outbox.Count.ShouldBe(1);
            view.Outbox.ShouldBe([context.Outbox[0]]);
            return Task.CompletedTask;
        });
    }

    /// <summary>T082 m3: ids and seeds are the shared context's, with the purpose prefixed by the initializer's name.</summary>
    [Fact]
    public async Task Ids_and_seeds_are_scoped_to_the_initializer()
    {
        await InAttemptAsync((context, tx) =>
        {
            var first = View(context, SeedModules.FirstOf([SeedAggregates.A]));
            var second = View(context, SeedModules.SecondOf([SeedAggregates.A]));

            first.NewId("x").ShouldBe(context.NewId("seed.first|x"));
            first.NewSeed("y").ShouldBe(context.NewSeed("seed.first|y"));
            first.NewId("x").ShouldNotBe(second.NewId("x"));
            first.NewSeed("y").ShouldNotBe(second.NewSeed("y"));
            first.NewId("x").ShouldNotBe(context.NewId("x"));
            return Task.CompletedTask;
        });
    }

    /// <summary>T082 m4: a key the creation already holds answers false without counting against the initializer's limit.</summary>
    [Fact]
    public async Task A_claim_key_already_held_answers_false_before_the_limit_check()
    {
        await InAttemptAsync(async (context, tx) =>
        {
            var first = View(context, SeedModules.FirstOf([SeedAggregates.A], maxClaimMarkers: 1));
            var second = View(context, SeedModules.SecondOf([SeedAggregates.A], maxClaimMarkers: 1));

            (await first.TryClaimAsync(SeedModules.ClaimCollection, "one")).ShouldBeTrue();
            (await first.TryClaimAsync(SeedModules.ClaimCollection, "one")).ShouldBeFalse();
            (await second.TryClaimAsync(SeedModules.ClaimCollection, "one")).ShouldBeFalse();
            (await second.TryClaimAsync(SeedModules.ClaimCollection, "two")).ShouldBeTrue();
            context.Claims.Count.ShouldBe(2);
        });
    }

    /// <summary>T082 m4: the context's own claim rule (a forbidden collection) is a creation rule, not an initializer throw.</summary>
    [Theory]
    [InlineData("receipts")]
    [InlineData("pa_seed_a")]
    public async Task A_claim_in_a_forbidden_collection_is_a_creation_rule(string collection)
    {
        await InAttemptAsync(async (context, tx) =>
        {
            var view = View(context, SeedModules.FirstOf([SeedAggregates.A], maxClaimMarkers: 1));

            var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await view.TryClaimAsync(collection, "k"));

            ex.ShouldBeOfType<AccountCreationRuleException>();
            ex.Message.ShouldBe($"Command 'identity.create_account (initializer seed.first)': Claim markers cannot live in collection '{collection}'.");
            context.Claims.ShouldBeEmpty();
        });
    }

    public void Dispose() => _h.Dispose();

    private static InitializerContext View(CommandContext context, AccountInitializerRegistration registration) =>
        new(context, registration, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Runs <paramref name="body"/> with a creation-shaped context (seed_a, seed_b; 2 claims) inside a store transaction.</summary>
    private Task InAttemptAsync(Func<CommandContext, ITransactionContext, Task> body) =>
        _h.Store.RunTransactionAsync(
            async tx =>
            {
                var loaded = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal)
                {
                    [SeedAggregates.A] = _h.Serializer.Load(SeedAggregates.ADescriptor, PlayerId, null),
                    [SeedAggregates.B] = _h.Serializer.Load(SeedAggregates.BDescriptor, PlayerId, null),
                };
                var actor = new ActorContext(PlayerId, ActorType.System, PlayerId, "test", "dev", new HashSet<string>(), "corr");
                var descriptor = new CommandDescriptor(AccountCreation.Operation, [SeedAggregates.A, SeedAggregates.B], maxClaimMarkers: 2);
                await body(new CommandContext(actor, _h.Clock.UtcNow, _h.Config, loaded, descriptor, tx, "receipt-1"), tx);
                return true;
            },
            new TransactionRunOptions(TimeSpan.FromSeconds(10)));
}
