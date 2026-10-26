using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Platform.Observability;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Log file per instance, rolling, append, redaction, bounded queue, sink errors (LOG-01..06, ACC-17, ACC-25).</summary>
public sealed class LoggingTests
{
    [Fact]
    public async Task Logs_are_compact_json_per_instance_with_request_and_trace_ids_and_masked_secrets()
    {
        string directory;
        string requestId = Guid.NewGuid().ToString("D");
        await using (var observed = await ObservedHost.StartAsync())
        {
            directory = observed.LogDirectory;
            var request = new HttpRequestMessage(HttpMethod.Get, ObservedHost.ProbeRoute + "?value=hello");
            request.Headers.Add("X-Request-Id", requestId);
            (await observed.Host.Client().SendAsync(request)).EnsureSuccessStatusCode();
        }

        var file = Directory.GetFiles(directory).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldStartWith("test-it-1-");
        var probe = ObservedHost.ReadShared(file).Split('\n')
            .Where(l => l.Contains("\"ProbeValue\"", StringComparison.Ordinal))
            .Select(l => JsonNode.Parse(l)!)
            .ShouldHaveSingleItem();

        probe["@t"]!.GetValue<string>().ShouldEndWith("Z");
        probe["InstanceId"]!.GetValue<string>().ShouldBe("it-1");
        probe["GameId"]!.GetValue<string>().ShouldBe("test");
        probe["RequestId"]!.GetValue<string>().ShouldBe(requestId);
        probe["@tr"]!.GetValue<string>().Length.ShouldBe(32);
        probe["ProbeValue"]!.GetValue<string>().ShouldBe("hello");
        probe["SessionToken"]!.GetValue<string>().ShouldBe(SensitiveDataRedactor.Mask);
        probe["Password"]!.GetValue<string>().ShouldBe(SensitiveDataRedactor.Mask);
        probe.ToJsonString().ShouldNotContain("should-not-appear");
    }

    [Fact]
    public async Task No_credential_reaches_the_log_files_after_the_main_flows()
    {
        string directory;
        string sessionToken;
        string secret;
        string? gmCookie;
        await using (var observed = await ObservedHost.StartAsync())
        {
            directory = observed.LogDirectory;
            var host = observed.Host;
            var secretBytes = IdentityHttp.NewSecret();
            secret = Convert.ToBase64String(secretBytes);
            var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), secretBytes);
            sessionToken = login.SessionToken;
            var player = new GamePlayer(host.BearerClient(sessionToken), login.PlayerId);
            await player.CheatAsync("gold", 100);
            await player.BuyAsync("potion_pack", 1);

            var gmLogin = await host.Client().PostAsJsonAsync(GmModule.LoginRoute, new { staffId = "admin", password = ObservedHost.StaffPassword });
            gmLogin.EnsureSuccessStatusCode();
            gmCookie = gmLogin.Headers.GetValues("Set-Cookie").First().Split(';')[0].Split('=')[1];
            var gm = host.Client();
            gm.DefaultRequestHeaders.Add("Cookie", "gs_gm=" + gmCookie);
            var adjust = new HttpRequestMessage(HttpMethod.Post, EconomyModule.AdjustRoute.Replace("{playerId}", login.PlayerId, StringComparison.Ordinal))
            {
                Content = JsonContent.Create(new { resource = "gold", delta = 5, reason = "log scan" }),
            };
            adjust.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
            (await gm.SendAsync(adjust)).EnsureSuccessStatusCode();

            // Failed logins are logged paths too.
            await host.Client().PostAsJsonAsync(GmModule.LoginRoute, new { staffId = "admin", password = "wrong-" + ObservedHost.StaffPassword });
        }

        var logs = ObservedHost.ReadAllLogs(directory);
        logs.Length.ShouldBeGreaterThan(0);
        foreach (var forbidden in new[] { sessionToken, secret, gmCookie!, ObservedHost.StaffPassword, ObservedHost.Pepper })
        {
            logs.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    public async Task Files_roll_by_size_keep_the_count_limit_and_restart_appends()
    {
        var directory = ObservedHost.NewLogDirectory();
        void Small(Dictionary<string, string?> s)
        {
            s["Observability:Logging:FileSizeLimitBytes"] = "4096";
            s["Observability:Logging:RetainedFileCountLimit"] = "3";
        }

        await using (var observed = await ObservedHost.StartAsync(directory, Small))
        {
            var logger = observed.Host.Services.GetRequiredService<ILogger<LoggingTests>>();
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("Rolling line {Index} {Padding}", i, new string('x', 64));
            }
        }

        var files = Directory.GetFiles(directory);
        files.Length.ShouldBe(3);
        files.ShouldAllBe(f => new FileInfo(f).Length <= 4096 + 1024);

        // A restart of the same instance appends to the newest file instead of overwriting it.
        var newest = files.OrderBy(f => f, StringComparer.Ordinal).Last();
        var before = ObservedHost.ReadShared(newest);
        await using (var observed = await ObservedHost.StartAsync(directory, s => s["Observability:Logging:FileSizeLimitBytes"] = "1048576"))
        {
            observed.Host.Services.GetRequiredService<ILogger<LoggingTests>>().LogInformation("After restart {Marker}", "restart-marker");
        }

        var after = ObservedHost.ReadShared(newest);
        after.ShouldStartWith(before);
        after.ShouldContain("restart-marker");
    }

    [Fact]
    public async Task A_full_queue_drops_and_counts_instead_of_blocking()
    {
        var dropped = LogSinkMonitor.Instance.DroppedCount;
        await using (var observed = await ObservedHost.StartAsync(settings: s => s["Observability:Logging:BufferSize"] = "1"))
        {
            var logger = observed.Host.Services.GetRequiredService<ILogger<LoggingTests>>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20_000; i++)
            {
                logger.LogInformation("Flood {Index}", i);
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            LogSinkMonitor.Instance.DroppedCount.ShouldBeGreaterThan(dropped);
        }
    }

    [Fact]
    public async Task Sink_errors_are_counted_and_make_readiness_degraded()
    {
        // A file where the log directory should be: the sink cannot create its file.
        var blocker = Path.Combine(Path.GetTempPath(), "gs-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "not a directory");
        var errors = LogSinkMonitor.Instance.SinkErrors;

        await using var observed = await ObservedHost.StartAsync(Path.Combine(blocker, "logs"));
        var logger = observed.Host.Services.GetRequiredService<ILogger<LoggingTests>>();
        for (var i = 0; i < 5 && LogSinkMonitor.Instance.SinkErrors == errors; i++)
        {
            logger.LogWarning("Cannot be written {Attempt}", i);
            await Task.Delay(200);
        }

        LogSinkMonitor.Instance.SinkErrors.ShouldBeGreaterThan(errors);
        var ready = await observed.Host.Client().GetAsync(ObservabilityModule.ReadyRoute);
        ((int)ready.StatusCode).ShouldBe(200);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("Degraded");
    }
}
