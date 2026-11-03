using GameServer.Actors;
using GameServer.Commands;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Layer 1 of FG-010 alone (FR-001, FR-002, FR-007, research R2, R5): the startup validators refuse every Dev endpoint and
/// every registered Dev command wherever development tools are not allowed, and nothing in Development/Test.
/// </summary>
public sealed class DevProfileValidationTests
{
    private const string Fix =
        "Fix: register Dev (cheat) modules only when development tools are allowed " +
        "(GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev)";

    public static TheoryData<string, string> Refused => new()
    {
        { "Staging", "beta" },
        { "Staging", "uat" },
        { "Production", "live" },
        { "Qa", "qa" },
    };

    public static TheoryData<string, string> Allowed => new()
    {
        { "Development", "local" },
        { "Development", "dev" },
        { "Test", "test" },
    };

    private static GameServerOptions Options(string deploymentEnvironment, string environmentId) =>
        new() { GameId = "test", EnvironmentId = environmentId, DeploymentEnvironment = deploymentEnvironment };

    private static RouteEndpoint Endpoint(string route, string method, SecurityProfile profile) =>
        new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            0,
            new EndpointMetadataCollection(new SecurityProfileMetadata(profile), new HttpMethodMetadata([method])),
            route);

    private static IReadOnlyList<Endpoint> Endpoints() =>
    [
        Endpoint("/api/v1/cheat/grant", "POST", SecurityProfile.Dev),
        Endpoint("/api/v1/counter/increment", "POST", SecurityProfile.Player),
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Dev_endpoint_is_reported_outside_development_and_test(string deploymentEnvironment, string environmentId)
    {
        var errors = EndpointSecurityValidator.Validate(Endpoints(), Options(deploymentEnvironment, environmentId));

        errors.ShouldBe([
            $"[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment '{deploymentEnvironment}' " +
            $"(environment id '{environmentId}'). {Fix} (OPS-09, SEC-08).",
        ]);
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Dev_endpoint_is_accepted_in_development_and_test(string deploymentEnvironment, string environmentId)
    {
        EndpointSecurityValidator.Validate(Endpoints(), Options(deploymentEnvironment, environmentId)).ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_deployment_environment_set_directly_on_the_options_does_not_allow_dev_endpoints()
    {
        var options = Options("Development", "dev");
        options.DeploymentEnvironment = "Qa";

        EndpointSecurityValidator.Validate(Endpoints(), options).ShouldHaveSingleItem().ShouldContain("deployment environment 'Qa'");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Dev_command_is_reported_for_every_registration_outside_development_and_test(string deploymentEnvironment, string environmentId)
    {
        var expected = $"command 'cheat.grant' has profile Dev in deployment environment '{deploymentEnvironment}' " +
            $"(environment id '{environmentId}'). {Fix} (OPS-09).";

        DevLines(Registry(serviceOnly: false), Options(deploymentEnvironment, environmentId)).ShouldBe(["[module economy.cheat] " + expected]);
        DevLines(Registry(serviceOnly: true), Options(deploymentEnvironment, environmentId)).ShouldBe(["[module economy.cheat] " + expected]);

        var noPlayerApi = Options(deploymentEnvironment, environmentId);
        noPlayerApi.Hosting.MapPlayerApi = false;
        DevLines(Registry(serviceOnly: false), noPlayerApi).ShouldBe(["[module economy.cheat] " + expected]);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Dev_command_without_route_is_reported_naming_module_and_operation(string deploymentEnvironment, string environmentId)
    {
        var registry = Registry(serviceOnly: false);
        registry.Add(new RoutelessDevModule(), serviceOnly: false);

        DevLines(registry, Options(deploymentEnvironment, environmentId)).ShouldBe([
            $"[module economy.cheat] command 'cheat.grant' has profile Dev in deployment environment '{deploymentEnvironment}' " +
            $"(environment id '{environmentId}'). {Fix} (OPS-09).",
            $"[module devtools] command 'devtools.reset' has profile Dev in deployment environment '{deploymentEnvironment}' " +
            $"(environment id '{environmentId}'). {Fix} (OPS-09).",
        ]);
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Dev_commands_are_accepted_in_development_and_test(string deploymentEnvironment, string environmentId)
    {
        var registry = Registry(serviceOnly: true);
        registry.Add(new RoutelessDevModule(), serviceOnly: false);

        DevLines(registry, Options(deploymentEnvironment, environmentId)).ShouldBeEmpty();
    }

    private static ModuleRegistry Registry(bool serviceOnly)
    {
        var registry = new ModuleRegistry();
        registry.Add(new IdentityModule(), serviceOnly: false);
        registry.Add(new EconomyModule(), serviceOnly: false);
        registry.Add(new CheatModule(), serviceOnly);
        return registry;
    }

    private static string[] DevLines(ModuleRegistry registry, GameServerOptions options) =>
        registry.Validate(options, null).Where(e => e.Contains("has profile Dev", StringComparison.Ordinal)).ToArray();

    /// <summary>A Dev command that no endpoint maps (job or GM-flow use).</summary>
    private sealed class RoutelessDevModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "devtools",
            DependsOn = [IdentityModule.ModuleName],
            Commands =
            [
                Command.Define<ResetCommand, ResetCommand>(new CommandDescriptor("devtools.reset", [IdentityAggregates.Profile], profile: SecurityProfile.Dev))
                    .HandledBy<ResetHandler>(),
            ],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed record ResetCommand;

    private sealed class ResetHandler : ICommandHandler<ResetCommand, ResetCommand>
    {
        public ValueTask<CommandOutcome<ResetCommand>> HandleAsync(ResetCommand request, ICommandContext context) =>
            ValueTask.FromResult(CommandOutcome<ResetCommand>.Ok(request));
    }
}
