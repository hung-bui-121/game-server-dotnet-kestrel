using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using SampleGame.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 011: the host switch, run modes and exit codes (contracts/operations-cli.md §1–§2, FR-029, FR-031, FR-038, D13).
/// Provision mode runs against a fake <see cref="IStoreProvisioner"/>; the Couchbase provisioner has its own integration tests.
/// </summary>
public sealed class GameServerCommandLineTests
{
    [Fact]
    public void The_provision_switch_selects_provision_mode_and_is_removed_from_the_host_arguments()
    {
        var parsed = GameServerCommandLine.Parse(["--urls", "x", "--provision", "--FileStore:ResetOnStart=true"]);

        parsed.Mode.ShouldBe(HostMode.Provision);
        parsed.HostArgs.ShouldBe(["--urls", "x", "--FileStore:ResetOnStart=true"]);
    }

    [Fact]
    public void Without_the_switch_the_host_serves()
    {
        var parsed = GameServerCommandLine.Parse(["--urls", "http://localhost:5080"]);

        parsed.Mode.ShouldBe(HostMode.Serve);
        parsed.HostArgs.ShouldBe(["--urls", "http://localhost:5080"]);
    }

    [Theory]
    [InlineData("--PROVISION")]
    [InlineData("--Provision")]
    public void The_switch_matches_in_any_letter_case(string spelled)
    {
        var parsed = GameServerCommandLine.Parse(["--urls", "x", spelled]);

        parsed.Mode.ShouldBe(HostMode.Provision);
        parsed.HostArgs.ShouldBe(["--urls", "x"]);
    }

    /// <summary>T055 m4: the configuration provider would read these as a key and the host would serve instead of provisioning.</summary>
    [Theory]
    [InlineData("--provision=true", "--provision=…")]
    [InlineData("--Provision=secret-looking-value", "--Provision=…")]
    [InlineData("/provision", "/provision")]
    [InlineData("/provision=1", "/provision=…")]
    [InlineData("-provision", "-provision")]
    public void A_misspelled_switch_is_refused_without_echoing_its_value(string spelled, string named)
    {
        var ex = Should.Throw<GameServerStartupException>(() => GameServerCommandLine.Parse(["--urls", "x", spelled]));

        ex.Violations.ShouldHaveSingleItem().ShouldBe(
            $"[provision] '{named}' is not the provisioning switch. Fix: write --provision alone, without a value (contracts/operations-cli.md §1).");
        ex.Message.ShouldNotContain("secret-looking-value");
    }

    [Fact]
    public void Values_and_other_keys_that_mention_provision_are_ordinary_arguments()
    {
        var parsed = GameServerCommandLine.Parse(["--GameServer:GameId", "provision", "--provisioning=1"]);

        parsed.Mode.ShouldBe(HostMode.Serve);
        parsed.HostArgs.ShouldBe(["--GameServer:GameId", "provision", "--provisioning=1"]);
    }

    [Fact]
    public async Task Provisioning_success_prints_each_item_and_the_summary()
    {
        var provisioner = new FakeProvisioner(
            new("scope", "g_test", ProvisioningOutcome.AlreadyPresent, TimeSpan.Zero),
            new("collection", "g_test.txn_meta", ProvisioningOutcome.Created, TimeSpan.Zero),
            new("index", "ix_a on receipts", ProvisioningOutcome.Created, TimeSpan.FromSeconds(14)),
            new("index", "ix_b on receipts", ProvisioningOutcome.AlreadyPresent, TimeSpan.Zero));

        var (code, output, error) = await ProvisionAsync(provisioner);

        code.ShouldBe(HostExitCodes.Ok, error);
        provisioner.Calls.ShouldBe(1);
        output.ShouldStartWith("provision: game test, environment test (Development), bucket ");
        output.ShouldContain("provision: scope g_test present");
        output.ShouldContain("provision: collection g_test.txn_meta created");
        output.ShouldContain("provision: index ix_a on receipts created (online after 14 s)");
        output.ShouldContain("provision: done: 2 created, 2 present");
    }

    [Fact]
    public async Task A_provisioning_failure_exits_2_with_the_failed_item()
    {
        var provisioner = new FakeProvisioner(new ProvisioningItem("scope", "g_test", ProvisioningOutcome.AlreadyPresent, TimeSpan.Zero))
        {
            Failure = new ProvisioningFailedException("index", "ix_x on receipts", "the index service is not available"),
        };

        var (code, output, _) = await ProvisionAsync(provisioner);

        code.ShouldBe(HostExitCodes.ProvisioningFailed);
        output.ShouldContain("provision: FAILED index ix_x on receipts: the index service is not available");
        output.ShouldContain("provision: stopped after 1 items; fix the cause and run again (idempotent)");
    }

    [Fact]
    public async Task A_provisioning_timeout_exits_2()
    {
        var provisioner = new FakeProvisioner { Delay = TimeSpan.FromMinutes(5) };

        var (code, output, _) = await ProvisionAsync(provisioner, s => s["Couchbase:ProvisioningTimeoutSeconds"] = "1");

        code.ShouldBe(HostExitCodes.ProvisioningFailed);
        output.ShouldContain("provision: FAILED timed out after 1 s (Couchbase:ProvisioningTimeoutSeconds)");
    }

    [Fact]
    public async Task An_invalid_deployment_identity_exits_1_without_calling_the_provisioner()
    {
        var provisioner = new FakeProvisioner();

        var (code, _, error) = await ProvisionAsync(provisioner, s =>
        {
            s["GameServer:EnvironmentId"] = "live";
            s["GameServer:DeploymentEnvironment"] = "Development";
        });

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ShouldContain("Game server startup validation failed:");
        error.ShouldContain("GameServer:EnvironmentId 'live'");
        provisioner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Production_provisioning_needs_no_sso_iap_pepper_or_master_key()
    {
        var provisioner = new FakeProvisioner(new ProvisioningItem("scope", "g_test", ProvisioningOutcome.Created, TimeSpan.Zero));

        var (code, output, error) = await ProvisionAsync(
            provisioner,
            s => s["GameServer:DeploymentEnvironment"] = "Production",
            modules: b => b.AddModule<IdentityModule>()
                .AddModule<EconomyModule>()
                .AddModule<IapModule>()
                .AddModule<SampleGameModule>()
                .AddModule<CounterModule>()
                .AddModule<GmModule>());

        code.ShouldBe(HostExitCodes.Ok, error);
        output.ShouldContain("environment test (Production)");
        provisioner.Calls.ShouldBe(1);
    }

    /// <summary>Slice 012 T043 (US4-8, FR-030): provisioning reads no analytics setting and creates no directory or lock.</summary>
    [Fact]
    public async Task Production_provisioning_with_analytics_files_needs_no_zone_and_creates_no_file()
    {
        var provisioner = new FakeProvisioner(new ProvisioningItem("scope", "g_test", ProvisioningOutcome.Created, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "gs-analytics-" + Guid.NewGuid().ToString("N"));

        var (code, _, error) = await ProvisionAsync(
            provisioner,
            s =>
            {
                s["GameServer:DeploymentEnvironment"] = "Production";
                s.Remove("GameServer:Analytics:TimeZone");
                s["GameServer:Analytics:Directory"] = directory;
            },
            modules: b => b.AddModule<IdentityModule>().AddAnalyticsFiles());

        code.ShouldBe(HostExitCodes.Ok, error);
        provisioner.Calls.ShouldBe(1);
        Directory.Exists(directory).ShouldBeFalse();
    }

    /// <summary>T055 m5: the provisioning credential is a secret; a JSON file holding it is refused outside Development/Test (OPS-08).</summary>
    [Fact]
    public async Task Provisioning_refuses_a_secret_read_from_a_json_file_outside_development()
    {
        var provisioner = new FakeProvisioner();
        var file = Path.Combine(Path.GetTempPath(), "gs-provision-secret-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, """{ "Couchbase": { "Password": "from-a-file" } }""");
        try
        {
            var app = TestHost.BuildApp(
                settings: s => s["GameServer:DeploymentEnvironment"] = "Production",
                testAuthentication: false,
                services: s => s.AddSingleton<IStoreProvisioner>(provisioner));
            ((IConfigurationBuilder)app.Configuration).AddJsonFile(file);

            var (code, _, error) = await RunProvisionAsync(app);

            code.ShouldBe(HostExitCodes.InvalidConfiguration);
            error.ShouldContain($"[deployment] secret 'Couchbase:Password' is present in {Path.GetFileName(file)}");
            error.ShouldNotContain("from-a-file");
            provisioner.Calls.ShouldBe(0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>T055 m2: a check that throws still gives one report naming the check; the other checks' errors are kept.</summary>
    [Fact]
    public async Task A_throwing_startup_check_joins_the_report_in_serve_and_provision_mode()
    {
        static void Checks(IServiceCollection services)
        {
            services.AddSingleton<IStartupCheck, ThrowingCheck>();
            services.AddSingleton<IStartupCheck>(new ReportingCheck("[test] the other check still reports"));
            services.AddSingleton<IStartupCheck>(new RefusingCheck("[test] a refusal thrown as a startup exception"));
        }

        var serve = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: Checks);
        });
        var provisioner = new FakeProvisioner();
        var (code, _, error) = await RunProvisionAsync(TestHost.BuildApp(services: s =>
        {
            Checks(s);
            s.AddSingleton<IStoreProvisioner>(provisioner);
        }));

        foreach (var report in new[] { serve.Message, error })
        {
            report.ShouldContain($"[{nameof(ThrowingCheck)}] the startup check failed: InvalidOperationException: {ThrowingCheck.Cause}");
            report.ShouldContain("[test] the other check still reports");
            report.ShouldContain(" - [test] a refusal thrown as a startup exception");
        }

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        provisioner.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_value_the_binder_cannot_convert_is_a_startup_error_naming_the_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GameServer:Limits:PlayerTimeoutSeconds"] = "abc" })
            .Build();

        var ex = Should.Throw<GameServerStartupException>(() => configuration.GetSettings<GameServerOptions>(GameServerOptions.SectionName));

        ex.Violations.ShouldHaveSingleItem().ShouldStartWith("[configuration] ");
        ex.Message.ShouldContain("GameServer:Limits:PlayerTimeoutSeconds");
        configuration.GetSettings<GameServerOptions>("Absent").ShouldNotBeNull();
    }

    [Fact]
    public async Task Provisioning_without_a_couchbase_store_exits_1()
    {
        var app = TestHost.BuildApp(testAuthentication: false);
        var (code, _, error) = await RunProvisionAsync(app);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ShouldContain("--provision needs GameServer:Store=Couchbase");
    }

    [Fact]
    public async Task Serve_mode_maps_a_startup_error_to_exit_1()
    {
        await using var app = TestHost.BuildApp(settings: s => s["GameServer:DeploymentEnvironment"] = "Staging");
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ServeAsync(app, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain("Game server startup validation failed:");
        error.ToString().ShouldContain("test authentication is enabled in 'Staging'");
    }

    [Fact]
    public void A_startup_exception_is_found_inside_aggregate_and_inner_exceptions()
    {
        var startup = new GameServerStartupException("[test] x");

        GameServerRunExtensions.FindStartupException(new AggregateException(new InvalidOperationException("a"), new InvalidOperationException("b", startup)))
            .ShouldBeSameAs(startup);
        GameServerRunExtensions.FindStartupException(new InvalidOperationException("no")).ShouldBeNull();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080", false)]
    [InlineData("http://[::]:5080", false)]
    [InlineData("http://*:5080", false)]
    [InlineData("http://+:5080", false)]
    [InlineData("http://192.168.1.5:5080", false)]
    [InlineData("http://myhost:5080", false)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://[::1]:5080", true)]
    public void Listen_addresses_are_classified(string address, bool loopback) => ListenAddresses.IsLoopback(address).ShouldBe(loopback);

    [Fact]
    public void The_listen_warning_needs_development_tools_and_a_network_address()
    {
        var development = new GameServerOptions { DeploymentEnvironment = "Development" };
        var staging = new GameServerOptions { DeploymentEnvironment = "Staging" };

        ListenAddresses.Warning(development, ["http://localhost:5080", "http://0.0.0.0:5080"]).ShouldNotBeNull().ShouldContain("http://0.0.0.0:5080");
        ListenAddresses.Warning(development, ["http://localhost:5080", "http://[::1]:5080"]).ShouldBeNull();
        ListenAddresses.Warning(staging, ["http://0.0.0.0:5080"]).ShouldBeNull();
    }

    private static Task<(int Code, string Output, string Error)> ProvisionAsync(
        FakeProvisioner provisioner,
        Action<Dictionary<string, string?>>? settings = null,
        Action<GameServerBuilder>? modules = null) =>
        RunProvisionAsync(TestHost.BuildApp(
            modules: modules,
            settings: settings,
            testAuthentication: false,
            services: s => s.AddSingleton<IStoreProvisioner>(provisioner)));

    private static async Task<(int Code, string Output, string Error)> RunProvisionAsync(WebApplication app)
    {
        await using (app)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await ProvisioningRunner.RunAsync(app, output, error, CancellationToken.None);
            return (code, output.ToString(), error.ToString());
        }
    }

    private sealed class ThrowingCheck : IStartupCheck
    {
        public const string Cause = "the check has a bug";

        public void Check(StartupCheckResult result) => throw new InvalidOperationException(Cause);
    }

    private sealed class ReportingCheck(string error) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add(error);
    }

    private sealed class RefusingCheck(string violation) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => throw new GameServerStartupException(violation);
    }

    private sealed class FakeProvisioner(params ProvisioningItem[] items) : IStoreProvisioner
    {
        public int Calls { get; private set; }

        public TimeSpan Delay { get; init; }

        public ProvisioningFailedException? Failure { get; init; }

        public async Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken)
        {
            Calls++;
            foreach (var item in items)
            {
                progress.Report(item);
            }

            await Task.Delay(Delay, cancellationToken);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
