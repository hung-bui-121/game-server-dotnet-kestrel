using GameServer.Accounts;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T050 (FR-032, SC-006, US4-4, BC-2): command settings out of range are lines of the one aggregated startup report, next
/// to an initializer line and an unrelated fault, in serve mode (startup exception, exit 1) and in provision mode (exit 1, nothing
/// provisioned).
/// </summary>
public sealed class CommandSettingsStartupTests
{
    private static readonly string[] SettingsLines =
    [
        "[deployment] GameServer:Commands:ReplayWindowHours is 0; allowed 1..168 (CMD-07).",
        "[deployment] GameServer:Commands:PerPlayerQueueLimit is 17; allowed 0..16 (CMD-06).",
        "[deployment] GameServer:Commands:ReceiptMaxBytes is 1000; allowed 1112..65536 (CMD-03).",
        "[deployment] GameServer:Commands:ResultCompactMaxBytes is 20000; allowed 64..16384 (CMD-03).",
        "[deployment] GameServer:Commands:ResultCompactMaxBytes 20000 needs 27692 receipt bytes (base64 + 1024) but ReceiptMaxBytes is 1000. " +
        "Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.",
        "[deployment] GameServer:Commands:MaxAggregatesPerGame is 0; allowed 1..16 (DAT-09).",
    ];

    private const string InitializerLine =
        "[module test.bad_settings_initializer] account initializer 'test.bad' touches 'profile'. Fix: remove it; the framework writes the profile of a new account.";

    private const string UnrelatedLine = "[deployment] GameServer:GameId 'Bad Id' is missing or invalid.";

    [Fact]
    public async Task Every_settings_line_joins_one_report_in_serve_mode()
    {
        var serve = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(modules: Modules, settings: BadSettings);
        });

        ShouldListEveryLine(serve.Message);
    }

    [Fact]
    public async Task Serve_mode_exits_1_with_every_settings_line()
    {
        await using var app = SampleTestHost.BuildApp(modules: Modules, settings: BadSettings);
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ServeAsync(app, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        ShouldListEveryLine(error.ToString());
    }

    [Fact]
    public async Task Provision_mode_reports_the_same_lines_and_provisions_nothing()
    {
        var provisioner = new CountingProvisioner();
        await using var app = SampleTestHost.BuildApp(
            modules: Modules,
            settings: BadSettings,
            testAuthentication: false,
            services: s => s.AddSingleton<IStoreProvisioner>(provisioner));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProvisioningRunner.RunAsync(app, output, error, CancellationToken.None);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        ShouldListEveryLine(error.ToString());
        provisioner.Calls.ShouldBe(0);
    }

    private static void ShouldListEveryLine(string report)
    {
        foreach (var line in SettingsLines)
        {
            report.ShouldContain(line);
        }

        report.ShouldContain(InitializerLine);
        report.ShouldContain(UnrelatedLine);

        // Registry order (errors-and-report-lines §4): account initializers, then the command settings.
        report.IndexOf(InitializerLine, StringComparison.Ordinal).ShouldBeLessThan(report.IndexOf(SettingsLines[0], StringComparison.Ordinal));
        report.ShouldNotContain("player aggregates registered (max 0)");
    }

    private static void BadSettings(Dictionary<string, string?> settings)
    {
        settings["GameServer:GameId"] = "Bad Id";
        settings["GameServer:Commands:ReplayWindowHours"] = "0";
        settings["GameServer:Commands:PerPlayerQueueLimit"] = "17";
        settings["GameServer:Commands:ReceiptMaxBytes"] = "1000";
        settings["GameServer:Commands:ResultCompactMaxBytes"] = "20000";
        settings["GameServer:Commands:MaxAggregatesPerGame"] = "0";
    }

    private static void Modules(GameServerBuilder builder)
    {
        SampleTestHost.DefaultModules(builder);
        builder.AddModule<EconomyModule>().AddModule(new BadInitializerModule());
    }

    /// <summary>A module whose only initializer touches the profile (one startup line).</summary>
    private sealed class BadInitializerModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "test.bad_settings_initializer",
            AccountInitializers = [AccountInitializerRegistration.Of<StartingStateInitializer>("test.bad", ["profile"])],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class CountingProvisioner : IStoreProvisioner
    {
        public int Calls { get; private set; }

        public Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
