using System.Net;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Auth;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Tests;

/// <summary>
/// The shipped sample host boots on the real local flavor (<c>appsettings.Local.json</c>: file store, Development tools, GM staff
/// seed) with its data folder moved to a temporary folder, never the developer's <c>_database</c> (research R12). Disposing a
/// factory releases the folder lock, so a second factory on the same folder is a restart (US1, US2-1, US2-9, FR-028).
/// </summary>
public sealed class SampleHostSmokeTests : IDisposable
{
    private const string LocalStaffId = "admin";
    private const string LocalStaffPassword = "dev-only-change-me";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-smoke-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestStores.DeleteFolder(_folder);

    /// <summary>
    /// The outbox sweeper stays on only where asked: it does not await the store initializer and relies on the key ring opening
    /// the file store first.
    /// </summary>
    private WebApplicationFactory<Program> LocalFlavor(bool outboxSweeper = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Local")
            .UseSetting("FileStore:Folder", _folder)
            .UseSetting("GameServer:Outbox:SweeperEnabled", outboxSweeper ? "true" : "false")
            .UseSetting("GameServer:Jobs:Enabled", "false")
            .UseSetting("GameServer:Hosting:DrainDelaySeconds", "0"));

    [Fact]
    public async Task Sample_host_serves_time_command_and_snapshot_on_the_local_flavor()
    {
        await using var factory = LocalFlavor(outboxSweeper: true);
        var client = factory.CreateClient();
        (await client.GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, "smoke");
        var command = await (await client.PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 2 }, ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>();
        command.Code.ShouldBe("OK");
        command.ConfigVersion.ShouldStartWith("sample-1+");

        var snapshot = await (await client.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();
        snapshot.Sections.Single(s => s.Id == "counter").Rev.ShouldBe(1);

        // The data lives in the configured folder (the command line wins over the flavor file's ../_database/dbbase).
        File.Exists(Path.Combine(_folder, "store.json")).ShouldBeTrue();
        Directory.EnumerateFiles(Path.Combine(_folder, "g_sample", "receipts")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_restart_on_the_same_folder_keeps_the_player_and_accepts_the_old_token()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        string token;
        string playerId;
        await using (var first = LocalFlavor())
        {
            var login = await first.CreateClient().LoginAsync(localId, secret);
            (token, playerId) = (login.SessionToken, login.PlayerId);
            var response = await Bearer(first, token).PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 2 }, ProtobufHttp.NewRequestId());
            (await response.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        }

        await using var second = LocalFlavor();
        var snapshot = await (await Bearer(second, token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();
        snapshot.Sections.Single(s => s.Id == "counter").Rev.ShouldBe(1);
        (await second.CreateClient().LoginAsync(localId, secret)).PlayerId.ShouldBe(playerId);
    }

    [Fact]
    public async Task A_gm_session_signed_in_with_the_local_seed_survives_a_restart()
    {
        var cookies = new CookieContainer();
        await using (var first = LocalFlavor())
        {
            var gm = await first.Server.GmLoginAsync(LocalStaffId, LocalStaffPassword, cookies);
            (await gm.GetAsync(GmModule.MeRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Same cookie, new process: the session and the key ring that protects the cookie were read back from the folder.
        await using var second = LocalFlavor();
        var browser = second.Server.Browser(cookies);
        (await browser.GetAsync(GmModule.MeRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
