using System.Net;
using GameServer.Actors;
using GameServer.Errors;
using GameServer.Modules.Economy;
using GameServer.Modules.Economy.Protocol.V1;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Hosting;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// FG-010 flavor matrix (FR-001, FR-002, FR-005, FR-007, US1-1, US1-2, US1-5, SC-001): a composition with the cheat module
/// starts and grants in the development flavors, and fails startup with the Dev endpoint and Dev command lines in every
/// other flavor; service-only and no-player-API registrations fail on the command line alone.
/// </summary>
public sealed class DevProfileFlavorMatrixTests
{
    private const string EndpointLine = "[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment";
    private const string CommandLine = "[module economy.cheat] command 'cheat.grant' has profile Dev in deployment environment";

    public static TheoryData<string, string> DevelopmentFlavors => new()
    {
        { "local", "Development" },
        { "dev", "Development" },
        { "test", "Test" },
    };

    public static TheoryData<string, string> OtherFlavors => new()
    {
        { "beta", "Staging" },
        { "uat", "Staging" },
        { "live", "Production" },
    };

    private static Action<Dictionary<string, string?>> Flavor(string environmentId, string deploymentEnvironment) => s =>
    {
        s["GameServer:EnvironmentId"] = environmentId;
        s["GameServer:DeploymentEnvironment"] = deploymentEnvironment;
    };

    private static Action<GameServerBuilder> WithCheat(bool serviceOnly = false) => b =>
    {
        b.AddModule<IdentityModule>().AddModule<EconomyModule>();
        if (serviceOnly)
        {
            b.AddModule<CheatModule>(o => o.ServiceOnly());
        }
        else
        {
            b.AddModule<CheatModule>();
        }
    };

    [Theory]
    [MemberData(nameof(DevelopmentFlavors))]
    public async Task Development_flavors_start_and_grant_for_players_and_gm_actors(string environmentId, string deploymentEnvironment)
    {
        await using var host = await SampleTestHost.StartAsync(modules: WithCheat(), settings: Flavor(environmentId, deploymentEnvironment));

        var player = await host.PlayerClient("p1").PostProtobufAsync(CheatModule.GrantRoute, new CheatGrantRequest { Resource = "gold", Quantity = 7 }, ProtobufHttp.NewRequestId());
        player.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await player.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");

        // A GM actor passes the pipeline (authenticated, not refused by profile); the HTTP route has no target player for a GM,
        // so GM flows reach a Dev command through the runner with a target player, as in v0.6.0.
        var gmHttp = await host.GmClient("staff", "economy.grant").PostProtobufAsync(CheatModule.GrantRoute, new CheatGrantRequest { Resource = "gold", Quantity = 1 }, ProtobufHttp.NewRequestId());
        gmHttp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await gmHttp.ReadErrorAsync()).Code.ShouldBe("BAD_REQUEST");
        var gm = new ActorContext("staff", ActorType.Gm, "p1", "test", environmentId, new HashSet<string>(), "corr");
        var execution = await host.Services.GetRequiredService<ICommandRunner>().ExecuteAsync(
            new CommandInvocation(CheatGrantHandler.Operation, gm, Guid.NewGuid().ToString("D"), "hash", new CheatGrantCommand("gold", 3), _ => [1]),
            CancellationToken.None);
        execution.Code.ShouldBe(CoreErrorCodes.Ok);

        var ledger = host.Ledger("p1");
        ledger.Count.ShouldBe(2);
        ledger.ShouldAllBe(entry => entry["payload"]!["sourceType"]!.GetValue<string>() == CheatGrantHandler.SourceType);
    }

    [Theory]
    [MemberData(nameof(OtherFlavors))]
    public async Task Other_flavors_fail_startup_with_the_dev_endpoint_and_command_lines(string environmentId, string deploymentEnvironment)
    {
        var lines = await DevLinesAsync(WithCheat(), Flavor(environmentId, deploymentEnvironment));

        var where = $" '{deploymentEnvironment}' (environment id '{environmentId}').";
        lines.Length.ShouldBe(2);
        lines.ShouldContain(l => l.StartsWith(EndpointLine + where, StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith(CommandLine + where, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OtherFlavors))]
    public async Task Service_only_dev_module_fails_startup_on_the_command_line(string environmentId, string deploymentEnvironment)
    {
        var lines = await DevLinesAsync(WithCheat(serviceOnly: true), Flavor(environmentId, deploymentEnvironment));

        lines.ShouldHaveSingleItem().ShouldStartWith(CommandLine + $" '{deploymentEnvironment}' (environment id '{environmentId}').");
    }

    [Theory]
    [MemberData(nameof(OtherFlavors))]
    public async Task Dev_module_without_player_api_fails_startup_on_the_command_line(string environmentId, string deploymentEnvironment)
    {
        var lines = await DevLinesAsync(WithCheat(), s =>
        {
            Flavor(environmentId, deploymentEnvironment)(s);
            s["GameServer:Hosting:MapPlayerApi"] = "false";
        });

        lines.ShouldHaveSingleItem().ShouldStartWith(CommandLine + $" '{deploymentEnvironment}' (environment id '{environmentId}').");
    }

    [Theory]
    [MemberData(nameof(DevelopmentFlavors))]
    public async Task Service_only_and_no_player_api_variants_start_in_development_flavors(string environmentId, string deploymentEnvironment)
    {
        await using (await SampleTestHost.StartAsync(modules: WithCheat(serviceOnly: true), settings: Flavor(environmentId, deploymentEnvironment)))
        {
        }

        await using (await SampleTestHost.StartAsync(modules: WithCheat(), settings: s =>
        {
            Flavor(environmentId, deploymentEnvironment)(s);
            s["GameServer:Hosting:MapPlayerApi"] = "false";
        }))
        {
        }
    }

    /// <summary>Startup report lines about the Dev profile; other violations of a non-development in-memory host are ignored.</summary>
    private static async Task<string[]> DevLinesAsync(Action<GameServerBuilder> modules, Action<Dictionary<string, string?>> settings)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(modules: modules, settings: settings);
        });
        return ex.Violations.Where(v => v.StartsWith(EndpointLine, StringComparison.Ordinal) || v.StartsWith(CommandLine, StringComparison.Ordinal)).ToArray();
    }
}
