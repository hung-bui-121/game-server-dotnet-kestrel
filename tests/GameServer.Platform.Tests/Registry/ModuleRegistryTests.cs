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

    private sealed class DoubleIncrementHandler : ICommandHandler<IncrementCommand, IncrementResult>
    {
        public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context) =>
            ValueTask.FromResult(CommandOutcome<IncrementResult>.Ok(new IncrementResult(request.By * 2)));
    }
}
