using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Config;
using GameServer.Platform.Registry;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Registry;

/// <summary>Registration rules enforced before traffic (ARC-06, ARC-07, DAT-09, API-11, SEC-07).</summary>
public sealed class ModuleRegistryTests
{
    private static readonly GameServerOptions ValidOptions = new() { GameId = "test", EnvironmentId = "dev" };

    private static ModuleRegistry Registry(params IGameModule[] modules)
    {
        var registry = new ModuleRegistry();
        foreach (var module in modules)
        {
            registry.Add(module, serviceOnly: false);
        }

        return registry;
    }

    private static DescriptorModule Module(string name, Func<ModuleDescriptor, ModuleDescriptor>? edit = null)
    {
        var descriptor = new ModuleDescriptor { Name = name };
        return new DescriptorModule(edit?.Invoke(descriptor) ?? descriptor);
    }

    private static IReadOnlyList<string> Validate(ModuleRegistry registry, GameServerOptions? options = null) =>
        registry.Validate(options ?? ValidOptions, null);

    [Fact]
    public void Valid_registration_has_no_violations()
    {
        Validate(Registry(TestModules.Identity(), TestModules.Counter(), TestModules.Probe())).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("", "dev")]
    [InlineData("test", "")]
    [InlineData("Bad-Id", "dev")]
    public void Missing_or_invalid_deployment_ids_fail(string gameId, string environmentId)
    {
        var errors = Validate(Registry(TestModules.Identity()), new GameServerOptions { GameId = gameId, EnvironmentId = environmentId });

        errors.ShouldContain(e => e.Contains("[deployment]") && e.Contains("ARC-10"));
    }

    [Fact]
    public void Missing_profile_aggregate_fails()
    {
        Validate(Registry(Module("x"))).ShouldContain(e => e.Contains("'profile'"));
    }

    [Fact]
    public void Missing_dependency_names_module_and_fix()
    {
        var errors = Validate(Registry(TestModules.Counter()));

        errors.ShouldContain(e => e.Contains("[module sample.counter]") && e.Contains("'identity'") && e.Contains("Fix:"));
    }

    [Fact]
    public void Duplicate_module_fails()
    {
        Validate(Registry(TestModules.Identity(), TestModules.Identity())).ShouldContain(e => e.Contains("registered twice"));
    }

    [Fact]
    public void Duplicate_command_handler_fails()
    {
        var dup = Module("dup", d => new ModuleDescriptor
        {
            Name = d.Name,
            Commands = [Command.Define<IncrementCommand, IncrementResult>(IncrementHandler.Descriptor).HandledBy<IncrementHandler>()],
        });

        Validate(Registry(TestModules.Identity(), TestModules.Counter(), dup))
            .ShouldContain(e => e.Contains("'counter.increment' is already registered"));
    }

    [Fact]
    public void Too_many_touches_or_unknown_aggregate_fails()
    {
        var bad = Module("bad", d => new ModuleDescriptor
        {
            Name = d.Name,
            Commands =
            [
                Command.Define<IncrementCommand, IncrementResult>(
                    new CommandDescriptor("bad.five", ["profile", "counter", "a", "b", "c"])).HandledBy<IncrementHandler>(),
            ],
        });

        var errors = Validate(Registry(TestModules.Identity(), TestModules.Counter(), bad));

        errors.ShouldContain(e => e.Contains("touches 5 aggregates"));
        errors.ShouldContain(e => e.Contains("unknown aggregate 'a'"));
    }

    [Fact]
    public void More_than_sixteen_aggregates_fails()
    {
        var many = Module("many", d => new ModuleDescriptor
        {
            Name = d.Name,
            Aggregates = Enumerable.Range(0, 16)
                .Select(i => (PlayerAggregateDescriptor)new PlayerAggregateDescriptor<CounterState>($"agg{i:00}", 1, false, () => new CounterState()))
                .ToArray(),
        });

        Validate(Registry(TestModules.Identity(), many)).ShouldContain(e => e.Contains("17 player aggregates"));
    }

    [Fact]
    public void Missing_upgrader_fails()
    {
        var noUpgrader = Module("v3", d => new ModuleDescriptor
        {
            Name = d.Name,
            Aggregates = [new PlayerAggregateDescriptor<CounterState>("vthree", 3, false, () => new CounterState())],
        });

        Validate(Registry(TestModules.Identity(), noUpgrader)).ShouldContain(e => e.Contains("upgrader from v1"));
    }

    [Fact]
    public void Error_code_range_overlap_and_duplicates_fail()
    {
        var clash = Module("clash", d => new ModuleDescriptor
        {
            Name = d.Name,
            ErrorCodes = new ErrorCodeSet(10050, 10150, new ErrorCode(10060, "COUNTER_LIMIT_EXCEEDED", 200, false, "dup")),
        });
        var core = Module("core-clash", d => new ModuleDescriptor
        {
            Name = d.Name,
            ErrorCodes = new ErrorCodeSet(500, 600, new ErrorCode(500, "bad name", 200, false, "x")),
        });

        var errors = Validate(Registry(TestModules.Identity(), TestModules.Counter(), clash, core));

        errors.ShouldContain(e => e.Contains("[module clash]") && e.Contains("overlaps 'sample.counter'"));
        errors.ShouldContain(e => e.Contains("'COUNTER_LIMIT_EXCEEDED' duplicates"));
        errors.ShouldContain(e => e.Contains("[module core-clash]") && e.Contains("overlaps 'core'"));
        errors.ShouldContain(e => e.Contains("'bad name' is invalid"));
    }

    [Fact]
    public void Gm_command_permission_must_be_in_catalog()
    {
        var gm = Module("gmmod", d => new ModuleDescriptor
        {
            Name = d.Name,
            Commands =
            [
                Command.Define<IncrementCommand, IncrementResult>(
                    new CommandDescriptor("gmmod.adjust", ["counter"], profile: SecurityProfile.Gm, permission: "nope")).HandledBy<IncrementHandler>(),
            ],
        });

        Validate(Registry(TestModules.Identity(), TestModules.Counter(), gm)).ShouldContain(e => e.Contains("permission 'nope'"));
    }

    [Fact]
    public void Required_config_file_must_exist()
    {
        var needsConfig = Module("needs", d => new ModuleDescriptor { Name = d.Name, RequiredConfigFiles = ["shop.json"] });
        var config = new GameConfig("t", "t+0", new Dictionary<string, byte[]>(), new HashSet<string>());

        Registry(TestModules.Identity(), needsConfig).Validate(ValidOptions, config)
            .ShouldContain(e => e.Contains("required config file 'shop.json'") && e.Contains("CFG-01"));
    }

    [Fact]
    public void ReplaceHandler_of_unknown_command_is_reported()
    {
        var registry = Registry(TestModules.Identity());
        registry.ReplaceHandler("nope.cmd", typeof(IncrementHandler));

        Validate(registry).ShouldContain(e => e.Contains("ReplaceHandler('nope.cmd')"));
    }

    [Fact]
    public void ReplaceHandler_swaps_the_handler_type()
    {
        var registry = Registry(TestModules.Identity(), TestModules.Counter());
        registry.ReplaceHandler(IncrementHandler.Operation, typeof(DoubleIncrementHandler));

        registry.GetCommand(IncrementHandler.Operation).HandlerType.ShouldBe(typeof(DoubleIncrementHandler));
        Validate(registry).ShouldBeEmpty();
    }

    private static IReadOnlyList<string> ValidateWith(params IGameModule[] extra) =>
        Validate(Registry([TestModules.Identity(), TestModules.Counter(), .. extra]));

    [Fact]
    public void Module_collection_without_deletion_treatment_fails_naming_module_and_collection()
    {
        var bad = Module("bag", d => new ModuleDescriptor { Name = d.Name, Collections = [new CollectionSpec("bag_items")] });

        ValidateWith(bad).ShouldContain(e => e.Contains("[module bag]") && e.Contains("'bag_items'") && e.Contains("no deletion treatment"));
    }

    [Fact]
    public void Projection_target_without_collection_spec_or_treatment_fails()
    {
        var noSpec = Module("feed", d => new ModuleDescriptor { Name = d.Name, OutboxProjections = [new OutboxProjection("feed.kind", "feed_log")] });
        var noTreatment = Module("feed2", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("feed2_log")],
            OutboxProjections = [new OutboxProjection("feed2.kind", "feed2_log")],
        });

        var errors = ValidateWith(noSpec, noTreatment);

        errors.ShouldContain(e => e.Contains("[module feed]") && e.Contains("'feed_log'") && e.Contains("no CollectionSpec"));
        errors.ShouldContain(e => e.Contains("[module feed2]") && e.Contains("'feed2_log'") && e.Contains("no deletion treatment"));
    }

    [Fact]
    public void Conflicting_treatments_fail_and_an_index_only_redeclaration_is_allowed()
    {
        var a = Module("a", d => new ModuleDescriptor { Name = d.Name, Collections = [new CollectionSpec("shared_log", Deletion: DeletionTreatment.Anonymize)] });
        var b = Module("b", d => new ModuleDescriptor { Name = d.Name, Collections = [new CollectionSpec("shared_log", Deletion: DeletionTreatment.Remove)] });
        var indexOnly = Module("c", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("shared_log", [new IndexSpec("ix_shared_log_at", "at")])],
        });

        ValidateWith(a, b).ShouldContain(e => e.Contains("[module b]") && e.Contains("'shared_log'") && e.Contains("module 'a' declares Anonymize"));
        ValidateWith(a, indexOnly).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("receipts", DeletionTreatment.Remove)]
    [InlineData("gm_audit", DeletionTreatment.NoPlayerData)]
    [InlineData("pa_profile", DeletionTreatment.Anonymize)]
    [InlineData("account_deletions", DeletionTreatment.NoPlayerData)]
    [InlineData("leases", DeletionTreatment.Remove)]
    public void Platform_collection_redeclared_with_another_treatment_fails(string collection, DeletionTreatment treatment)
    {
        var bad = Module("bad", d => new ModuleDescriptor { Name = d.Name, Collections = [new CollectionSpec(collection, Deletion: treatment)] });

        ValidateWith(bad).ShouldContain(e => e.Contains("[module bad]") && e.Contains($"'{collection}' is owned by the platform"));
    }

    [Fact]
    public void Platform_collection_redeclared_with_its_own_treatment_is_allowed()
    {
        var same = Module("same", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("receipts", Deletion: DeletionTreatment.Anonymize), new CollectionSpec("gm_audit")],
        });

        ValidateWith(same).ShouldBeEmpty();
    }

    [Fact]
    public void Module_step_treatment_without_a_step_fails()
    {
        var bad = Module("stepless", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("stepless_data", Deletion: DeletionTreatment.ModuleStep)],
        });
        var good = Module("stepped", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("stepped_data", Deletion: DeletionTreatment.ModuleStep)],
            DeletionSteps = [new DeletionStepRegistration("stepped.erase", typeof(NoOpDeletionStep))],
        });

        ValidateWith(bad).ShouldContain(e => e.Contains("[module stepless]") && e.Contains("'stepless_data'") && e.Contains("no deletion step"));
        ValidateWith(good).ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_player_field_fails()
    {
        var bad = Module("pf", d => new ModuleDescriptor
        {
            Name = d.Name,
            Collections = [new CollectionSpec("pf_log", Deletion: DeletionTreatment.Remove, PlayerField: "player id")],
        });

        ValidateWith(bad).ShouldContain(e => e.Contains("[module pf]") && e.Contains("'pf_log'") && e.Contains("invalid player field"));
    }

    [Fact]
    public void Duplicate_step_name_and_step_type_without_the_contract_fail()
    {
        var a = Module("sa", d => new ModuleDescriptor { Name = d.Name, DeletionSteps = [new DeletionStepRegistration("shared.erase", typeof(NoOpDeletionStep))] });
        var b = Module("sb", d => new ModuleDescriptor
        {
            Name = d.Name,
            DeletionSteps = [new DeletionStepRegistration("shared.erase", typeof(NoOpDeletionStep)), new DeletionStepRegistration("sb.bad", typeof(string))],
        });

        var errors = ValidateWith(a, b);

        errors.ShouldContain(e => e.Contains("[module sb]") && e.Contains("'shared.erase' is already registered by module 'sa'"));
        errors.ShouldContain(e => e.Contains("[module sb]") && e.Contains("'sb.bad'") && e.Contains("does not implement IAccountDeletionStep"));
    }

    [Fact]
    public void Duplicate_bad_or_untyped_jobs_fail_naming_module_and_job()
    {
        var a = Module("ja", d => new ModuleDescriptor { Name = d.Name, Jobs = [new JobDescriptor("shared.job", typeof(NoOpJob), 60)] });
        var b = Module("jb", d => new ModuleDescriptor
        {
            Name = d.Name,
            Jobs =
            [
                new JobDescriptor("shared.job", typeof(NoOpJob), 60),
                new JobDescriptor("Bad Name", typeof(NoOpJob), 60),
                new JobDescriptor("jb.untyped", typeof(string), 60),
                new JobDescriptor("jb.fast", typeof(NoOpJob), 1),
            ],
        });

        var errors = ValidateWith(a, b);

        errors.ShouldContain(e => e.Contains("[module jb]") && e.Contains("job 'shared.job' is already registered by module 'ja'"));
        errors.ShouldContain(e => e.Contains("[module jb]") && e.Contains("job name 'Bad Name' is invalid"));
        errors.ShouldContain(e => e.Contains("[module jb]") && e.Contains("'jb.untyped'") && e.Contains("does not implement ILeaseJob"));
        errors.ShouldContain(e => e.Contains("[module jb]") && e.Contains("'jb.fast'") && e.Contains("default interval"));
        ValidateWith(a).ShouldBeEmpty();
    }

    [Fact]
    public void A_job_override_must_name_a_registered_job()
    {
        var registry = Registry(TestModules.Identity(), TestModules.Counter(), Module("ja", d => new ModuleDescriptor { Name = d.Name, Jobs = [new JobDescriptor("ja.job", typeof(NoOpJob), 60)] }));
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Jobs.Settings["ja.job"] = new GameServer.Platform.Jobs.JobOverride { IntervalSeconds = 60 };

        Validate(registry, options).ShouldBeEmpty();

        options.Jobs.Settings["ja.jbo"] = new GameServer.Platform.Jobs.JobOverride { Enabled = false };
        Validate(registry, options).ShouldHaveSingleItem().ShouldBe("[jobs] GameServer:Jobs:Settings:ja.jbo names no registered job. Fix: use one of: ja.job.");
    }

    [Fact]
    public void Job_and_purge_settings_outside_their_bounds_fail()
    {
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Jobs.LeaseSeconds = 20;
        options.Jobs.RenewSeconds = 30;
        options.Jobs.PollSeconds = 1;
        options.Jobs.Settings["x.job"] = new GameServer.Platform.Jobs.JobOverride { IntervalSeconds = 5 };
        options.Purge.BatchSize = 101;
        options.Purge.MaxRetryDelayMinutes = 5;

        var errors = Validate(Registry(TestModules.Identity()), options);

        errors.ShouldContain(e => e.Contains("GameServer:Jobs:LeaseSeconds (20)"));
        errors.ShouldContain(e => e.Contains("GameServer:Jobs:RenewSeconds (30)"));
        errors.ShouldContain(e => e.Contains("GameServer:Jobs:PollSeconds (1)"));
        errors.ShouldContain(e => e.Contains("GameServer:Jobs:Settings:x.job:IntervalSeconds (5)"));
        errors.ShouldContain(e => e.Contains("[jobs] GameServer:Jobs:Settings:x.job names no registered job"));
        errors.ShouldContain(e => e.Contains("GameServer:Purge:BatchSize (101)"));
        errors.ShouldContain(e => e.Contains("GameServer:Purge:MaxRetryDelayMinutes (5)"));
    }

    [Fact]
    public void Lease_must_outlive_three_transaction_expirations()
    {
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Transactions.ExpirationSeconds = 25;

        Validate(Registry(TestModules.Identity()), options).ShouldContain(e => e.Contains("GameServer:Jobs:LeaseSeconds (60)"));
    }

    [Fact]
    public void Deletion_plan_lists_platform_collections_first_then_modules_in_registration_order()
    {
        var registry = Registry(TestModules.Identity(), TestModules.Counter(), TestModules.Probe());

        var plan = registry.DeletionPlan().Select(d => (d.Collection, d.Treatment, d.Owner)).ToArray();

        plan.ShouldBe(
        [
            ("receipts", DeletionTreatment.Anonymize, "platform"),
            ("gm_audit", DeletionTreatment.Anonymize, "platform"),
            ("dataprotection", DeletionTreatment.NoPlayerData, "platform"),
            ("gate", DeletionTreatment.NoPlayerData, "platform"),
            ("leases", DeletionTreatment.NoPlayerData, "platform"),
            ("pa_profile", DeletionTreatment.Remove, "platform"),
            ("pa_counter", DeletionTreatment.Remove, "platform"),
            ("pa_probe", DeletionTreatment.Remove, "platform"),
            ("pa_probe_hidden", DeletionTreatment.Remove, "platform"),
            ("counter_log", DeletionTreatment.Anonymize, "sample.counter"),
            ("probe_claims", DeletionTreatment.Anonymize, "probe"),
        ]);
        registry.DeletionPlan().Where(d => d.IsPlayerAggregate).Select(d => d.Collection).ShouldAllBe(c => c.StartsWith("pa_"));
        registry.DeletionPlan().ShouldNotContain(d => d.Collection == "account_deletions");
        registry.RequiredCollections().ShouldContain("leases");
        registry.RequiredCollections().ShouldContain("account_deletions");
        registry.TransactionOnlyCollections().ShouldContain("account_deletions");
        registry.TransactionOnlyCollections().ShouldNotContain("leases");
    }

    private sealed class NoOpDeletionStep : GameServer.Privacy.IAccountDeletionStep
    {
        public ValueTask RunAsync(GameServer.Privacy.IPlayerDataEraser eraser, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoOpJob : GameServer.Jobs.ILeaseJob
    {
        public Task RunAsync(GameServer.Jobs.IJobContext context) => Task.CompletedTask;
    }

    private sealed class DoubleIncrementHandler : ICommandHandler<IncrementCommand, IncrementResult>
    {
        public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context) =>
            ValueTask.FromResult(CommandOutcome<IncrementResult>.Ok(new IncrementResult(request.By * 2)));
    }
}
