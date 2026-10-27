extern alias sdk;

using System.Text.Json.Nodes;
using GameServer.Platform.Gate;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// ACC-30 end to end: the real gate (document <c>gate</c>, store evaluator) answers the SDK, which raises
/// <c>Maintenance</c> / <c>UpdateRequired</c> with the server's details and does not retry.
/// </summary>
public sealed class GateClientTests
{
    private static async Task SetGateAsync(TestHost host, GateDocument gate)
    {
        var audited = host.Services.GetRequiredService<IAuditedStore>();
        await audited.RunAsync(async tx =>
        {
            var current = (await tx.GetManyAsync([GateDocument.Key]))[0];
            if (current is null)
            {
                await tx.InsertAsync(GateDocument.Key, gate.ToJson());
            }
            else
            {
                await tx.ReplaceAsync(current, gate.ToJson());
            }

            await tx.InsertAsync(new DocumentKey("gm_audit", "gm_audit::" + Guid.NewGuid().ToString("N")), new JsonObject { ["operation"] = "test.gate" });
            return 0;
        });
        host.Clock.Advance(TimeSpan.FromSeconds(11));
    }

    private static (GameServerClient Client, RecordingDelay Delay) NewClient(TestHost host, string version)
    {
        var http = host.Client();
        var delay = new RecordingDelay();
        var client = new GameServerClient(
            new GameServerClientOptions(http.BaseAddress!, version, "editor", "build-42"),
            new HttpClientTransport(http),
            delay: delay);
        return (client, delay);
    }

    [Fact]
    public async Task The_sdk_reports_maintenance_and_required_updates_from_the_real_gate()
    {
        await using var host = await TestHost.StartAsync(GameHttp.FullGame);
        var (localId, secret) = SdkHarness.NewDevice();

        await SetGateAsync(host, new GateDocument
        {
            Maintenance = new MaintenanceState { Enabled = true, Message = "Back soon", ExpectedEndUtc = host.Clock.UtcNow.AddMinutes(10) },
        });
        var (client, delay) = NewClient(host, "1.2.3");
        GameServerError? maintenance = null;
        client.Maintenance += e => maintenance = e;
        (await Should.ThrowAsync<GameServerException>(() => client.LoginGuestAsync(localId, secret))).Error.Code.ShouldBe(ErrorCodes.Maintenance);
        maintenance.ShouldNotBeNull();
        maintenance.Details["message"].ShouldBe("Back soon");
        maintenance.RetryAfterMs.ShouldBe(589_000); // 10 min to the expected end, minus the 11 s the test waited for the gate cache

        await SetGateAsync(host, new GateDocument
        {
            MinClientVersions = new Dictionary<string, MinClientVersion>(StringComparer.Ordinal)
            {
                ["editor"] = new() { MinVersion = "2.0.0", UpdateUrl = "https://store.example/update" },
            },
        });
        GameServerError? update = null;
        client.UpdateRequired += e => update = e;
        (await Should.ThrowAsync<GameServerException>(() => client.LoginGuestAsync(localId, secret))).Error.HttpStatus.ShouldBe(426);
        update.ShouldNotBeNull();
        update.Details["updateUrl"].ShouldBe("https://store.example/update");
        delay.Delays.ShouldBeEmpty();

        var (current, _) = NewClient(host, "2.0.0");
        await current.LoginGuestAsync(localId, secret);
        current.HasSession.ShouldBeTrue();
    }
}
