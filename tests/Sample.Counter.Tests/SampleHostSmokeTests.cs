using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Auth;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Tests;

/// <summary>
/// The shipped sample host boots on the real local flavor (<c>appsettings.Local.json</c>: file store, Development tools, GM staff
/// seed) through the kit's <see cref="LocalFlavorFactory{TEntryPoint}"/>: data, analytics and logs in the factory's own temporary
/// root, never the developer's <c>_database</c> or the content root (research R8, FR-012). Disposing a factory releases the folder
/// lock, so a second factory on the same folder (<see cref="LocalFlavorOptions.Folder"/>) is a restart (US1, US2-1, US2-9, FR-028).
/// </summary>
public sealed class SampleHostSmokeTests : IDisposable
{
    private const string LocalStaffId = "admin";
    private const string LocalStaffPassword = "dev-only-change-me";

    /// <summary>Root shared by the factories of one test (restart cases); the factories never delete it.</summary>
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-smoke-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestStores.DeleteFolder(_folder);

    /// <summary>
    /// The outbox sweeper stays on only where asked: it does not await the store initializer and relies on the key ring opening
    /// the file store first.
    /// </summary>
    private static LocalFlavorFactory<Program> LocalFlavor(bool outboxSweeper = false, string? folder = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var options = new LocalFlavorOptions { OutboxSweeper = outboxSweeper, Folder = folder };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            options.Settings[key] = value;
        }

        return new LocalFlavorFactory<Program>(options);
    }

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

        // The data lives in the factory's data folder (the command line wins over the flavor file's ../_database/dbbase).
        File.Exists(Path.Combine(factory.DataFolder, "store.json")).ShouldBeTrue();
        Directory.EnumerateFiles(Path.Combine(factory.DataFolder, "g_sample", "receipts")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_restart_on_the_same_folder_keeps_the_player_and_accepts_the_old_token()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        string token;
        string playerId;
        await using (var first = LocalFlavor(folder: _folder))
        {
            var login = await first.CreateClient().LoginAsync(localId, secret);
            (token, playerId) = (login.SessionToken, login.PlayerId);
            var response = await Bearer(first, token).PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 2 }, ProtobufHttp.NewRequestId());
            (await response.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        }

        await using var second = LocalFlavor(folder: _folder);
        var snapshot = await (await Bearer(second, token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();
        snapshot.Sections.Single(s => s.Id == "counter").Rev.ShouldBe(1);
        (await second.CreateClient().LoginAsync(localId, secret)).PlayerId.ShouldBe(playerId);
    }

    [Fact]
    public async Task A_gm_session_signed_in_with_the_local_seed_survives_a_restart()
    {
        var cookies = new CookieContainer();
        await using (var first = LocalFlavor(folder: _folder))
        {
            var gm = await first.Server.GmLoginAsync(LocalStaffId, LocalStaffPassword, cookies);
            (await gm.GetAsync(GmModule.MeRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Same cookie, new process: the session and the key ring that protects the cookie were read back from the folder.
        await using var second = LocalFlavor(folder: _folder);
        var browser = second.Server.Browser(cookies);
        (await browser.GetAsync(GmModule.MeRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Slice 012 T050 (US5-1, SC-006, FR-035): with no game code beyond the sample, a login and two fake purchases end up in the
    /// login and payment files of the analytics folder, with the documented keys and no credential, token or payload in them.
    /// </summary>
    [Fact]
    public async Task The_local_flavor_writes_login_and_payment_files_without_any_secret()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var validToken = "smoke-token-" + Guid.NewGuid().ToString("N");
        var noOrderToken = "smoke-noorder-" + Guid.NewGuid().ToString("N");
        string sessionToken;
        string instanceId;
        string analyticsFolder;
        await using (var factory = LocalFlavor(folder: _folder))
        {
            (instanceId, analyticsFolder) = (factory.InstanceId, factory.AnalyticsFolder);
            var login = await factory.CreateClient().LoginAsync(localId, secret);
            sessionToken = login.SessionToken;
            var player = new GamePlayer(Bearer(factory, sessionToken), login.PlayerId);
            (await player.IapAsync("google", "gems_100", "valid:" + validToken)).Code.ShouldBe("OK");
            (await player.IapAsync("google", "gems_100", "noorder:" + noOrderToken)).Code.ShouldBe("OK");
        }

        var files = Directory.GetFiles(analyticsFolder, "*.jsonl");
        var day = files.Select(Path.GetFileNameWithoutExtension).Select(n => n![^8..]).Distinct().ShouldHaveSingleItem();
        var loginLines = File.ReadAllLines(Path.Combine(analyticsFolder, $"sample-{instanceId}-login-{day}.jsonl"));
        var paymentLines = File.ReadAllLines(Path.Combine(analyticsFolder, $"sample-{instanceId}-payment-{day}.jsonl"));
        files.Length.ShouldBe(2);
        File.Exists(Path.Combine(analyticsFolder, $".sample-{instanceId}.lock")).ShouldBeTrue();

        var loginRecord = JsonNode.Parse(loginLines.ShouldHaveSingleItem())!.AsObject();
        loginRecord.Select(p => p.Key).ShouldBe(
            ["event", "schemaVersion", "eventId", "gameId", "environmentId", "userID", "activeTime", "activeTimeUtc", "timeZone", "utcOffset", "source", "provider", "isNewUser"]);
        loginRecord["environmentId"]!.GetValue<string>().ShouldBe("local");
        loginRecord["timeZone"]!.GetValue<string>().ShouldBe("Asia/Ho_Chi_Minh");
        paymentLines.Length.ShouldBe(2);
        foreach (var line in paymentLines)
        {
            var payment = JsonNode.Parse(line)!.AsObject();
            payment["isTest"]!.GetValue<bool>().ShouldBeTrue();
            payment.Select(p => p.Key).Take(11).ShouldBe(
                ["event", "schemaVersion", "eventId", "gameId", "environmentId", "userID", "chargeTime", "chargeTimeUtc", "timeZone", "utcOffset", "receiptId"]);
        }

        // SC-006: no session token, guest secret, purchase token or receipt payload anywhere in the files. The fake verifier
        // derives the order id of a valid: purchase from its reference (GPA.{ref}), so that reference appears as the order id by
        // construction; the token-only path is the noorder: purchase (digest, never the token).
        string[] secrets =
        [
            sessionToken, Convert.ToBase64String(secret), Convert.ToHexString(secret), Convert.ToHexStringLower(secret), localId,
            noOrderToken, "noorder:" + noOrderToken, "valid:" + validToken,
        ];
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            secrets.ShouldAllBe(s => !text.Contains(s, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Disabled_analytics_creates_no_folder_and_relays_purchase_receipts()
    {
        await using var factory = LocalFlavor(settings: new Dictionary<string, string?> { ["GameServer:Analytics:Enabled"] = "false" });
        var login = await factory.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = new GamePlayer(Bearer(factory, login.SessionToken), login.PlayerId);

        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));

        purchase.Code.ShouldBe("OK");
        Directory.Exists(factory.AnalyticsFolder).ShouldBeFalse();
        var receipt = Directory.GetFiles(Path.Combine(factory.DataFolder, "g_sample", "receipts"))
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!)
            .Single(r => r.ToJsonString().Contains(purchase.ReceiptId, StringComparison.Ordinal));
        receipt.ToJsonString().ShouldContain("\"outboxPending\":false");
    }

    /// <summary>Slice 013 T027 (edge case "two factories in parallel"): distinct roots, instance ids and locks.</summary>
    [Fact]
    public async Task Two_factories_in_parallel_use_distinct_roots_and_instance_ids()
    {
        await using var first = LocalFlavor();
        await using var second = LocalFlavor();

        var responses = await Task.WhenAll(first.CreateClient().GetAsync("/v1/time"), second.CreateClient().GetAsync("/v1/time"));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        first.Root.ShouldNotBe(second.Root);
        first.InstanceId.ShouldNotBe(second.InstanceId);
        foreach (var factory in new[] { first, second })
        {
            Path.GetDirectoryName(factory.Root).ShouldBe(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            Path.GetFileName(factory.Root).ShouldStartWith("gs-local-");
            factory.InstanceId.ShouldMatch("^lf-[0-9a-f]{8}$");
            factory.AppliedSettings["Observability:InstanceId"].ShouldBe(factory.InstanceId);
            factory.AppliedSettings["FileStore:Folder"].ShouldBe(factory.DataFolder);
        }
    }

    [Fact]
    public async Task After_dispose_the_root_is_gone_unless_it_was_given()
    {
        string root;
        await using (var owned = LocalFlavor())
        {
            root = owned.Root;
            await owned.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            Directory.Exists(owned.DataFolder).ShouldBeTrue();
        }

        Directory.Exists(root).ShouldBeFalse();

        await using (var given = LocalFlavor(folder: _folder))
        {
            await given.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        }

        File.Exists(Path.Combine(_folder, "data", "store.json")).ShouldBeTrue();
    }

    /// <summary>
    /// Slice 013 T027 (FR-012): a login, <c>/v1/time</c> and a command write data, logs and analytics only under the factory's
    /// root — nothing named after this instance or player appears in the sample's content root, the sample's default data
    /// folder or the test output, and the test output gets no data/analytics/logs folder.
    /// </summary>
    [Fact]
    public async Task A_session_writes_nothing_outside_the_factory_root()
    {
        string contentRoot;
        string instanceId;
        string playerId;
        await using (var factory = LocalFlavor())
        {
            contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            instanceId = factory.InstanceId;
            var login = await factory.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            playerId = login.PlayerId;
            (await factory.CreateClient().GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);
            var command = await (await Bearer(factory, login.SessionToken)
                .PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId()))
                .ReadProtobufAsync<CommandResponse>();
            command.Code.ShouldBe("OK");

            // The markers are real: the player's aggregate and this instance's log file exist inside the root.
            Named(factory.DataFolder, playerId).ShouldNotBeEmpty();
            Named(factory.LogFolder, instanceId).ShouldNotBeEmpty();
        }

        string[] outside = [contentRoot, Path.GetFullPath(Path.Combine(contentRoot, "..", "_database")), AppContext.BaseDirectory];
        foreach (var folder in outside)
        {
            Named(folder, instanceId).ShouldBeEmpty();
            Named(folder, playerId).ShouldBeEmpty();
        }

        foreach (var name in new[] { "data", "analytics", "logs" })
        {
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, name)).ShouldBeFalse();
        }

        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.jsonl", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.log", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>Files whose name contains <paramref name="marker"/>, skipping build output folders of a project directory.</summary>
    private static List<string> Named(string folder, string marker)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) || f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).Contains(marker, StringComparison.Ordinal))
            .ToList();
    }

    private static HttpClient Bearer(LocalFlavorFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
