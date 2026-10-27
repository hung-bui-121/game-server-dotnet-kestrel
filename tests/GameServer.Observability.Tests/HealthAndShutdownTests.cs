using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Platform.Commands;
using GameServer.Platform.Observability;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Live/ready, GM health detail, shutdown (OBS-03, OPS-03, ACC-28, ACC-51).</summary>
public sealed class HealthAndShutdownTests
{
    private static async Task<(int Status, string Body)> GetAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpClient> AdminAsync(TestHost host) => host.GmLoginAsync("admin", ObservedHost.StaffPassword);

    [Fact]
    public async Task Live_and_ready_answer_with_the_status_only()
    {
        await using var observed = await ObservedHost.StartAsync();
        var client = observed.Host.Client();
        (await GetAsync(client, ObservabilityModule.LiveRoute)).ShouldBe((200, "Healthy"));

        // Another test may have produced a recent sink error (process-wide): Degraded is still ready.
        var (status, body) = await GetAsync(client, ObservabilityModule.ReadyRoute);
        status.ShouldBe(200);
        body.ShouldBeOneOf("Healthy", "Degraded");
    }

    [Fact]
    public async Task Store_outage_makes_ready_503_without_details_and_gm_sees_the_failing_check()
    {
        await using var observed = await ObservedHost.StartAsync();
        var admin = await AdminAsync(observed.Host);

        // The GM was signed in before the outage: session and staff are served from the per-instance cache.
        (await GetAsync(admin, GmModule.MeRoute)).Status.ShouldBe(200);
        observed.Host.Store.Faults.Unavailable = true;

        var client = observed.Host.Client();
        (await GetAsync(client, ObservabilityModule.ReadyRoute)).ShouldBe((503, "Unhealthy"));
        (await GetAsync(client, ObservabilityModule.LiveRoute)).ShouldBe((200, "Healthy"));

        var detail = JsonNode.Parse((await GetAsync(admin, GmModule.HealthRoute)).Body)!;
        var store = detail["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "store")!;
        store["status"]!.GetValue<string>().ShouldBe("Unhealthy");
        detail["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "config")!["status"]!.GetValue<string>().ShouldBe("Healthy");

        observed.Host.Store.Faults.Unavailable = false;
        (await GetAsync(client, ObservabilityModule.ReadyRoute)).Status.ShouldBe(200);
    }

    [Fact]
    public async Task Ready_results_are_shared_for_the_cache_window()
    {
        await using var observed = await ObservedHost.StartAsync(settings: s => s["Observability:ReadyCacheMilliseconds"] = "60000");
        var client = observed.Host.Client();
        (await GetAsync(client, ObservabilityModule.ReadyRoute)).Status.ShouldBe(200);

        // A flood of Public ready calls does not reach the store again within the window.
        observed.Host.Store.Faults.Unavailable = true;
        (await GetAsync(client, ObservabilityModule.ReadyRoute)).Status.ShouldBe(200);
        observed.Host.Store.Faults.Unavailable = false;
    }

    [Fact]
    public async Task Stop_keeps_serving_with_ready_503_during_the_drain_delay()
    {
        var observed = await ObservedHost.StartAsync(settings: s => s["GameServer:Hosting:DrainDelaySeconds"] = "2");
        try
        {
            var client = observed.Host.Client();
            var stopping = observed.Host.App.StopAsync();
            await Task.Delay(300);
            stopping.IsCompleted.ShouldBeFalse();
            (await GetAsync(client, ObservabilityModule.ReadyRoute)).ShouldBe((503, "Unhealthy"));
            (await GetAsync(client, "/v1/time")).Status.ShouldBe(200);
            await stopping;
        }
        finally
        {
            await observed.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_detail_requires_ops_read()
    {
        await using var observed = await ObservedHost.StartAsync(settings: s =>
        {
            s["Gm:Bootstrap:Staff:1:StaffId"] = "support";
            s["Gm:Bootstrap:Staff:1:Password"] = ObservedHost.StaffPassword;
            s["Gm:Bootstrap:Staff:1:Roles:0"] = "Support";
        });
        var support = await observed.Host.GmLoginAsync("support", ObservedHost.StaffPassword);
        (await GetAsync(support, GmModule.HealthRoute)).Status.ShouldBe(403);
    }

    [Fact]
    public async Task Ready_turns_503_as_soon_as_shutdown_starts()
    {
        await using var observed = await ObservedHost.StartAsync();
        observed.Host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        (await GetAsync(observed.Host.Client(), ObservabilityModule.ReadyRoute)).ShouldBe((503, "Unhealthy"));
    }

    [Fact]
    public async Task A_command_committing_during_stop_completes_with_its_receipt()
    {
        var observed = await ObservedHost.StartAsync();
        try
        {
            var login = await observed.Host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            var player = new GamePlayer(observed.Host.BearerClient(login.SessionToken), login.PlayerId);
            observed.Host.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(500);
            observed.Host.Store.Faults.Enqueue(StoreFault.DelayCommit);

            var command = player.CheatAsync("gold", 42);
            await Task.Delay(100);
            var stopping = observed.Host.App.StopAsync();

            var response = await command;
            await stopping;
            response.Code.ShouldBe("OK");
            observed.Host.Store.Peek(ReceiptDocument.KeyFor(response.ReceiptId)).ShouldNotBeNull();
            observed.Host.Store.Peek(new GameServer.Platform.Persistence.DocumentKey("pa_wallet", login.PlayerId))!["state"]!["balances"]!["gold"]!
                .GetValue<long>().ShouldBe(42);
        }
        finally
        {
            await observed.DisposeAsync();
        }
    }
}
