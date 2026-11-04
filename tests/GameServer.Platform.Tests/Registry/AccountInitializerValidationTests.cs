using GameServer.Accounts;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Registry;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Registry;

/// <summary>
/// Slice 016 T023 (FR-015, SC-006, US2-5, BC-9, errors-and-report-lines §4.1): every fault of an initializer declaration is one
/// startup line; the block runs after the commands and before the error codes.
/// </summary>
public sealed class AccountInitializerValidationTests
{
    private static readonly GameServerOptions Options = new() { GameId = "test", EnvironmentId = "dev" };

    [Fact]
    public void Valid_initializers_have_no_line()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.B], 1), SeedModules.SecondOf([SeedAggregates.A], 1))).ShouldBeEmpty();
    }

    /// <summary>T082 m10: segments separated by single dots, each starting with a letter, and the module's first segment first.</summary>
    [Theory]
    [InlineData("Seed.first")]
    [InlineData("seed")]
    [InlineData("seed.")]
    [InlineData("seed.first-x")]
    [InlineData("seed..first")]
    [InlineData("seed.first.")]
    [InlineData("seed.1st")]
    [InlineData("seed.first._x")]
    [InlineData("other.first")]
    [InlineData("seedy.first")]
    public void An_invalid_name_is_one_line(string name)
    {
        Validate(Seed(AccountInitializerRegistration.Of<FirstSeedInitializer>(name, [SeedAggregates.A])))
            .ShouldBe([$"[module seed] account initializer '{name}' has an invalid name. Fix: use 'seed.<name>' in lowercase."]);
    }

    [Theory]
    [InlineData("seed.starting.gold")]
    [InlineData("seed.starting_gold2")]
    public void A_dotted_name_after_the_module_segment_is_valid(string name)
    {
        Validate(Seed(AccountInitializerRegistration.Of<FirstSeedInitializer>(name, [SeedAggregates.A]))).ShouldBeEmpty();
    }

    /// <summary>T082 m10: a dotted module name contributes its first segment (sample.game → sample.starting_gold).</summary>
    [Fact]
    public void A_dotted_module_name_contributes_its_first_segment()
    {
        static DescriptorModule Module(string module, string name) => new(new ModuleDescriptor
        {
            Name = module,
            Aggregates = [SeedAggregates.ADescriptor],
            AccountInitializers = [AccountInitializerRegistration.Of<FirstSeedInitializer>(name, [SeedAggregates.A])],
        });

        Validate(Module("seed.game", "seed.starting_gold")).ShouldBeEmpty();
        Validate(Module("seed.game", "seed.game.starting_gold")).ShouldBeEmpty();
        Validate(Module("seed.game", "game.starting_gold")).ShouldBe(
            ["[module seed.game] account initializer 'game.starting_gold' has an invalid name. Fix: use 'seed.<name>' in lowercase."]);
    }

    [Fact]
    public void A_name_registered_by_two_modules_is_one_line()
    {
        var other = new DescriptorModule(new ModuleDescriptor
        {
            Name = "seed.other",
            AccountInitializers = [AccountInitializerRegistration.Of<SecondSeedInitializer>(SeedModules.First, [SeedAggregates.B])],
        });

        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A])), other).ShouldBe([
            "[module seed.other] account initializer 'seed.first' is already registered by module 'seed'. Fix: initializer names must be unique per game.",
        ]);
    }

    [Fact]
    public void An_unknown_aggregate_is_one_line()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A, "nope"]))).ShouldBe([
            "[module seed] account initializer 'seed.first' touches unknown aggregate 'nope'. Fix: register the aggregate or fix Touches.",
        ]);
    }

    [Fact]
    public void A_duplicate_touch_is_one_line()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.A]))).ShouldBe([
            "[module seed] account initializer 'seed.first' declares aggregate 'seed_a' twice. Fix: list each aggregate once in Touches.",
        ]);
    }

    [Fact]
    public void Touching_the_profile_is_one_line()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A, "profile"]))).ShouldBe([
            "[module seed] account initializer 'seed.first' touches 'profile'. Fix: remove it; the framework writes the profile of a new account.",
        ]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void A_claim_count_outside_0_to_2_is_one_line(int claims)
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A], claims))).ShouldContain(
            $"[module seed] account initializer 'seed.first' allows {claims} claim markers. Fix: use 0..2 (DAT-04).");
    }

    [Fact]
    public void The_creation_budget_counts_the_profile_and_the_union_of_touches()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.B]), SeedModules.SecondOf([ProbeAggregates.Probe, ProbeAggregates.Hidden]))).ShouldBe([
            "[deployment] account creation touches 5 aggregates (profile + seed_a, seed_b, probe, probe_hidden from initializers seed.first, seed.second); " +
            "max is 4. Fix: move state into fewer aggregates or seed it in a later command (DAT-09).",
        ]);
    }

    [Fact]
    public void Two_initializers_declaring_the_same_aggregate_are_one_budget_entry()
    {
        var registry = Registry(Seed(
            SeedModules.FirstOf([SeedAggregates.A, SeedAggregates.B, ProbeAggregates.Probe]),
            SeedModules.SecondOf([ProbeAggregates.Probe, SeedAggregates.A])));

        registry.Validate(Options, null).ShouldBeEmpty();
        var creation = AccountCreation.Describe(registry);
        creation.Operation.ShouldBe("identity.create_account");
        creation.Touches.ShouldBe([SeedAggregates.A, SeedAggregates.B, ProbeAggregates.Probe]);
        creation.Profile.ShouldBe(SecurityProfile.Player);
        registry.AccountInitializers.Select(i => (i.Module, i.Registration.Name)).ShouldBe([("seed", SeedModules.First), ("seed", SeedModules.Second)]);
    }

    [Fact]
    public void The_claim_sum_over_every_initializer_is_one_line()
    {
        Validate(Seed(SeedModules.FirstOf([SeedAggregates.A], 2), SeedModules.SecondOf([SeedAggregates.B], 1))).ShouldBe([
            "[deployment] account initializers reserve 3 claim markers (seed.first: 2, seed.second: 1); max is 2. Fix: reserve fewer claims (DAT-04).",
        ]);
    }

    [Fact]
    public void The_creation_descriptor_sums_the_claims()
    {
        AccountCreation.Describe(Registry(Seed(SeedModules.FirstOf([SeedAggregates.A], 1), SeedModules.SecondOf([SeedAggregates.B], 1))))
            .MaxClaimMarkers.ShouldBe(2);
    }

    [Fact]
    public void A_module_command_with_the_reserved_operation_is_one_line()
    {
        var reserved = new DescriptorModule(new ModuleDescriptor
        {
            Name = "squatter",
            Commands = [Command.Define<Unit, Unit>(new CommandDescriptor(AccountCreation.Operation, [SeedAggregates.A])).HandledBy<NoChangeHandler>()],
        });

        Validate(Seed(), reserved).ShouldBe([
            "[module squatter] command 'identity.create_account' uses the operation reserved for account creation. Fix: rename the command.",
        ]);
    }

    [Fact]
    public void Every_fault_is_one_line_of_one_report_between_the_command_and_error_code_blocks()
    {
        var faulty = new DescriptorModule(new ModuleDescriptor
        {
            Name = "faulty",
            Aggregates = [new PlayerAggregateDescriptor<SeedState>("Bad-Id", 1, clientVisible: false, () => new SeedState())],
            ErrorCodes = new ErrorCodeSet(20300, 20399, new ErrorCode(20301, "bad_name", 200, false, "x")),
            Commands = [Command.Define<Unit, Unit>(new CommandDescriptor(AccountCreation.Operation, [SeedAggregates.A])).HandledBy<NoChangeHandler>()],
            AccountInitializers =
            [
                AccountInitializerRegistration.Of<FirstSeedInitializer>("Bad", ["profile", "nope", "nope"], maxClaimMarkers: 3),
                AccountInitializerRegistration.Of<SecondSeedInitializer>(SeedModules.First, [SeedAggregates.A, SeedAggregates.B, ProbeAggregates.Probe, ProbeAggregates.Hidden]),
            ],
        });

        var errors = Validate(Seed(SeedModules.FirstOf([SeedAggregates.A])), faulty);

        errors.ShouldBe([
            "[module faulty] aggregate id 'Bad-Id' is invalid. Fix: use [a-z][a-z0-9_]{1,31}.",
            "[module faulty] account initializer 'Bad' has an invalid name. Fix: use 'faulty.<name>' in lowercase.",
            "[module faulty] account initializer 'Bad' touches 'profile'. Fix: remove it; the framework writes the profile of a new account.",
            "[module faulty] account initializer 'Bad' touches unknown aggregate 'nope'. Fix: register the aggregate or fix Touches.",
            "[module faulty] account initializer 'Bad' declares aggregate 'nope' twice. Fix: list each aggregate once in Touches.",
            "[module faulty] account initializer 'Bad' allows 3 claim markers. Fix: use 0..2 (DAT-04).",
            "[module faulty] account initializer 'seed.first' has an invalid name. Fix: use 'faulty.<name>' in lowercase.",
            "[module faulty] account initializer 'seed.first' is already registered by module 'seed'. Fix: initializer names must be unique per game.",
            "[deployment] account creation touches 6 aggregates (profile + seed_a, nope, seed_b, probe, probe_hidden from initializers seed.first, Bad, seed.first); " +
            "max is 4. Fix: move state into fewer aggregates or seed it in a later command (DAT-09).",
            "[deployment] account initializers reserve 3 claim markers (Bad: 3); max is 2. Fix: reserve fewer claims (DAT-04).",
            "[module faulty] command 'identity.create_account' uses the operation reserved for account creation. Fix: rename the command.",
            "[module faulty] error code name 'bad_name' is invalid. Fix: use UPPER_SNAKE_CASE.",
        ]);
    }

    private static IReadOnlyList<string> Validate(params IGameModule[] extra) => Registry(extra).Validate(Options, null);

    private static ModuleRegistry Registry(params IGameModule[] extra)
    {
        var registry = new ModuleRegistry();
        foreach (var module in new IGameModule[] { TestModules.Identity(), TestModules.Counter(), TestModules.Probe() }.Concat(extra))
        {
            registry.Add(module, serviceOnly: false);
        }

        return registry;
    }

    private static DescriptorModule Seed(params AccountInitializerRegistration[] initializers) => SeedModules.Module(new SeedScript(), initializers);
}
