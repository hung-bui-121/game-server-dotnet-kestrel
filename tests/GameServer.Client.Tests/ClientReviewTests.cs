extern alias sdk;

using Google.Protobuf;
using sdk::GameServer.Client;
using Shouldly;
using Economy = sdk::GameServer.Modules.Economy.Protocol.V1;
using Identity = sdk::GameServer.Modules.Identity.Protocol.V1;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>Regression tests for the 006 tech-lead review (login retries, handler isolation, rollback, protocol checks, time).</summary>
public sealed class ClientReviewTests : IAsyncLifetime
{
    private const string Grant = "api/v1/cheat/grant";

    private SdkHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await SdkHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static Task<CommandResult<Economy.CheatGrantResult>> GrantAsync(GameServerClient client, long quantity) =>
        client.SendCommandAsync(Grant, new Economy.CheatGrantRequest { Resource = "gold", Quantity = quantity }, Economy.CheatGrantResult.Parser);

    private static bool Is(TransportRequest request, string route) => request.Url.AbsolutePath.TrimStart('/') == route;

    private async Task<(GameServerClient Client, ScriptedTransport Transport, RecordingDelay Delay)> LoggedInAsync()
    {
        var created = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await created.Client.LoginGuestAsync(localId, secret);
        return created;
    }

    [Fact]
    public async Task A_lost_login_is_not_retried_but_a_rate_limited_one_is()
    {
        var (client, transport, delay) = _harness.NewClient();
        transport.After = (request, _, response) => Is(request, GameServerClient.LoginRoute) ? throw new TransportException("lost") : response;
        var (localId, secret) = SdkHarness.NewDevice();

        (await Should.ThrowAsync<GameServerException>(() => client.LoginGuestAsync(localId, secret))).Error.Code.ShouldBe(ErrorCodes.NetworkError);
        transport.RequestsTo(GameServerClient.LoginRoute).Count().ShouldBe(1);
        delay.Delays.ShouldBeEmpty();

        transport.After = null;
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index == start ? ScriptedTransport.Error(429, ErrorCodes.RateLimited, true, 100) : null;
        await client.LoginGuestAsync(localId, secret);
        client.HasSession.ShouldBeTrue();
        transport.RequestsTo(GameServerClient.LoginRoute).Count().ShouldBe(3);
    }

    [Fact]
    public async Task Throwing_game_handlers_never_turn_a_committed_command_into_an_error()
    {
        var (client, _, _) = _harness.NewClient();
        var failures = new List<Exception>();
        client.HandlerFailed += failures.Add;
        client.ConfigVersionChanged += _ => throw new InvalidOperationException("game bug in config handler");
        client.Sections.SectionChanged += _ => throw new InvalidOperationException("game bug in section handler");
        var (localId, secret) = SdkHarness.NewDevice();

        await client.LoginGuestAsync(localId, secret);
        var result = await GrantAsync(client, 9);

        result.IsOk.ShouldBeTrue();
        client.Sections.Get("wallet", Economy.WalletState.Parser)!.Balances["gold"].ShouldBe(9);
        failures.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_late_snapshot_never_rolls_a_section_back()
    {
        var (client, transport, _) = await LoggedInAsync();
        await GrantAsync(client, 4);
        var wallet = client.Sections.Get("wallet", Economy.WalletState.Parser)!;

        transport.After = (request, _, response) =>
        {
            if (!Is(request, GameServerClient.SnapshotRoute))
            {
                return response;
            }

            var snapshot = SdkCore.SnapshotResponse.Parser.ParseFrom(response.Body);
            foreach (var section in snapshot.Sections.Where(s => s.Id == "wallet"))
            {
                section.Rev = 0;
                section.Payload = ByteString.Empty;
            }

            return new TransportResponse(200, response.ContentType, snapshot.ToByteArray());
        };
        await client.RefreshSectionsAsync(["wallet"]);

        client.Sections.RevOf("wallet").ShouldBe(1);
        client.Sections.Get("wallet", Economy.WalletState.Parser).ShouldBe(wallet);
    }

    [Fact]
    public async Task A_non_protobuf_200_is_retried_for_commands_and_rejected_for_login()
    {
        var (client, transport, _) = await LoggedInAsync();
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index == start ? new TransportResponse(200, "text/html", "<html>portal</html>"u8.ToArray()) : null;
        (await GrantAsync(client, 1)).IsOk.ShouldBeTrue();
        transport.RequestsTo(Grant).Count().ShouldBe(2);

        var (fresh, freshTransport, _) = _harness.NewClient();
        freshTransport.Before = (_, _) => new TransportResponse(200, "text/html", []);
        var (localId, secret) = SdkHarness.NewDevice();
        (await Should.ThrowAsync<GameServerException>(() => fresh.LoginGuestAsync(localId, secret))).Error.Code.ShouldBe(ErrorCodes.InvalidResponse);
        freshTransport.Exchanges.Count.ShouldBe(1);
        fresh.HasSession.ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_login_response_does_not_create_a_session()
    {
        var (client, transport, _) = _harness.NewClient();
        transport.Before = (_, _) => new TransportResponse(200, GameServerClient.ProtobufContentType, new Identity.LoginResponse().ToByteArray());
        var (localId, secret) = SdkHarness.NewDevice();
        (await Should.ThrowAsync<GameServerException>(() => client.LoginGuestAsync(localId, secret))).Error.Code.ShouldBe(ErrorCodes.InvalidResponse);
        client.HasSession.ShouldBeFalse();
    }

    [Fact]
    public async Task A_retry_after_beyond_the_cap_ends_the_call_instead_of_waiting()
    {
        var (client, transport, delay) = await LoggedInAsync();
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index == start ? ScriptedTransport.Error(503, ErrorCodes.RequestTimeout, true, retryAfterMs: 60_000) : null;

        (await Should.ThrowAsync<GameServerException>(() => GrantAsync(client, 1))).Error.RetryAfterMs.ShouldBe(60_000);
        delay.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retries_keep_the_token_of_the_first_attempt()
    {
        var session = new InMemorySessionStore();
        var (client, transport, _) = _harness.NewClient(session);
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) =>
        {
            if (index != start)
            {
                return null;
            }

            session.Token = "switched-account-token";
            return ScriptedTransport.Error(503, ErrorCodes.ConcurrentUpdate, true);
        };

        (await GrantAsync(client, 1)).IsOk.ShouldBeTrue();
        var sends = transport.RequestsTo(Grant).ToArray();
        sends[1].Headers["Authorization"].ShouldBe(sends[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task A_failed_refetch_after_a_commit_is_reported_not_thrown()
    {
        var (client, transport, _) = await LoggedInAsync();
        transport.After = (request, _, response) =>
        {
            if (Is(request, GameServerClient.SnapshotRoute))
            {
                throw new TransportException("snapshot lost");
            }

            if (!Is(request, Grant) || response.Status != 200)
            {
                return response;
            }

            var command = SdkCore.CommandResponse.Parser.ParseFrom(response.Body);
            foreach (var update in command.Updates)
            {
                update.Full = false;
                update.BaseRev = 41;
            }

            return new TransportResponse(200, response.ContentType, command.ToByteArray());
        };

        var result = await GrantAsync(client, 2);
        result.IsOk.ShouldBeTrue();
        result.SectionsStale.ShouldBeTrue();
        result.ReceiptId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Server_time_follows_the_monotonic_clock_not_device_clock_changes()
    {
        var clock = new ManualClientClock();
        var (client, _, _) = _harness.NewClient(clock: clock);
        var server = await client.SyncTimeAsync();
        (server - _harness.Host.Clock.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));

        clock.UtcNow += TimeSpan.FromHours(5);       // user changes the device clock
        client.ServerNow.ShouldBe(server);
        clock.Monotonic += TimeSpan.FromSeconds(10);  // real time passes
        client.ServerNow.ShouldBe(server + TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("http://localhost/game", 0.2)]
    [InlineData("http://localhost/", 1.5)]
    public void Invalid_options_are_refused(string baseUrl, double jitter)
    {
        var options = new GameServerClientOptions(new Uri(baseUrl), "1.0", "editor", "b") { Jitter = jitter };
        Should.Throw<ArgumentException>(() => new GameServerClient(options, new HttpClientTransport(new HttpClient())));
    }
}
