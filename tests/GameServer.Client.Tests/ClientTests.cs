extern alias sdk;

using GameServer.Platform.Testing;
using GameServer.Platform.Web.Middleware;
using Google.Protobuf;
using sdk::GameServer.Client;
using Shouldly;
using Economy = sdk::GameServer.Modules.Economy.Protocol.V1;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>SDK against the real framework host (SDK-02, SDK-03, ACC-04, ACC-20, ACC-34, ACC-35, ACC-43).</summary>
public sealed class ClientTests : IAsyncLifetime
{
    private const string Grant = "api/v1/cheat/grant";
    private const string Buy = "api/v1/shop/buy";

    private SdkHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await SdkHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task<(GameServerClient Client, ScriptedTransport Transport, RecordingDelay Delay)> LoggedInAsync()
    {
        var (client, transport, delay) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        return (client, transport, delay);
    }

    private static Task<CommandResult<Economy.CheatGrantResult>> GrantAsync(GameServerClient client, long quantity) =>
        client.SendCommandAsync(Grant, new Economy.CheatGrantRequest { Resource = "gold", Quantity = quantity }, Economy.CheatGrantResult.Parser);

    private long Gold(GameServerClient client) =>
        client.Sections.Get("wallet", Economy.WalletState.Parser)!.Balances["gold"];

    [Fact]
    public async Task Login_keeps_the_token_in_the_header_sections_with_revs_and_the_server_time()
    {
        var (client, transport, _) = await LoggedInAsync();

        client.HasSession.ShouldBeTrue();
        client.PlayerId.ShouldNotBeNullOrEmpty();
        client.Sections.RevOf("profile").ShouldBe(1);
        (client.ServerNow - _harness.Host.Clock.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));

        await client.RefreshSectionsAsync();
        var snapshot = transport.RequestsTo(GameServerClient.SnapshotRoute).Single();
        snapshot.Headers["Authorization"].ShouldStartWith("Bearer ");
        snapshot.Headers[GameServerClient.ClientVersionHeader].ShouldBe("1.2.3");
        snapshot.Headers[GameServerClient.PlatformHeader].ShouldBe("editor");
        snapshot.Headers[GameServerClient.BuildIdHeader].ShouldBe("build-42");
        Guid.TryParse(snapshot.Headers[GameServerClient.RequestIdHeader], out _).ShouldBeTrue();
        transport.RequestsTo(GameServerClient.LoginRoute).Single().Headers.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Fact]
    public void Sdk_gate_header_names_match_the_server_contract()
    {
        GameServerClient.ClientVersionHeader.ShouldBe(GateHeaders.ClientVersion);
        GameServerClient.PlatformHeader.ShouldBe(GateHeaders.Platform);
        GameServerClient.BuildIdHeader.ShouldBe(GateHeaders.BuildId);
        GameServerClient.RequestIdHeader.ShouldBe(CorrelationMiddleware.HeaderName);
    }

    [Fact]
    public async Task Commands_apply_section_updates_and_return_the_module_result()
    {
        var (client, _, _) = await LoggedInAsync();
        var result = await GrantAsync(client, 10);

        result.IsOk.ShouldBeTrue();
        result.ReceiptId.ShouldNotBeNullOrEmpty();
        result.Result!.BalanceAfter.ShouldBe(10);
        client.Sections.RevOf("wallet").ShouldBe(1);
        Gold(client).ShouldBe(10);
    }

    [Fact]
    public async Task A_lost_response_is_retried_with_the_same_bytes_and_replayed_once()
    {
        var (client, transport, _) = await LoggedInAsync();
        var start = transport.Exchanges.Count;
        transport.After = (request, index, response) =>
            index == start ? throw new TransportException("response lost") : response;

        var result = await GrantAsync(client, 10);

        result.Replayed.ShouldBeTrue();
        Gold(client).ShouldBe(10);
        var sends = transport.RequestsTo(Grant).ToArray();
        sends.Length.ShouldBe(2);
        sends[1].Body.ShouldBe(sends[0].Body);
        sends[1].Headers[GameServerClient.RequestIdHeader].ShouldBe(sends[0].Headers[GameServerClient.RequestIdHeader]);
        _harness.Host.Store.All("receipts").Count(r => r.Content["operation"]!.GetValue<string>() == "cheat.grant").ShouldBe(1);
    }

    [Fact]
    public async Task Commit_unknown_retry_resends_the_same_bytes_and_applies_once()
    {
        var (client, transport, delay) = await LoggedInAsync();
        _harness.Host.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        var result = await GrantAsync(client, 25);

        result.IsOk.ShouldBeTrue();
        result.Replayed.ShouldBeTrue();
        Gold(client).ShouldBe(25);
        var exchanges = transport.Exchanges.Where(e => e.Request.Url.AbsolutePath.EndsWith(Grant, StringComparison.Ordinal)).ToArray();
        exchanges[0].Response!.Status.ShouldBe(503);
        SdkCore.ErrorEnvelope.Parser.ParseFrom(exchanges[0].Response!.Body).Code.ShouldBe(ErrorCodes.CommitUnknownRetry);
        exchanges[1].Request.Body.ShouldBe(exchanges[0].Request.Body);
        delay.Delays.ShouldHaveSingleItem().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task Retry_after_is_respected_and_backoff_grows()
    {
        var (client, transport, delay) = await LoggedInAsync();
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index switch
        {
            _ when index == start => ScriptedTransport.Error(429, ErrorCodes.RateLimited, true, retryAfterMs: 300),
            _ when index == start + 1 => ScriptedTransport.Error(503, ErrorCodes.RequestTimeout, true),
            _ => null,
        };

        (await GrantAsync(client, 1)).IsOk.ShouldBeTrue();

        delay.Delays.Count.ShouldBe(2);
        delay.Delays[0].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
        delay.Delays[1].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400 * 0.8));
    }

    [Fact]
    public async Task Business_codes_and_non_retryable_errors_are_not_retried()
    {
        var (client, transport, delay) = await LoggedInAsync();

        // The sample game's contract comes from its own .proto (server-side generated types share Google.Protobuf).
        var buy = await client.SendCommandAsync(
            Buy,
            new SampleGame.Protocol.V1.BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 },
            SampleGame.Protocol.V1.BuyItemResult.Parser);
        buy.Code.ShouldBe("INSUFFICIENT_FUNDS");
        buy.Result.ShouldBeNull();

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            client.SendCommandAsync(Grant, new Economy.CheatGrantRequest { Resource = string.Empty, Quantity = 1 }, Economy.CheatGrantResult.Parser));
        ex.Error.Code.ShouldBe(ErrorCodes.BadRequest);

        transport.RequestsTo(Buy).Count().ShouldBe(1);
        transport.RequestsTo(Grant).Count().ShouldBe(1);
        delay.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Network_errors_give_up_after_the_retry_budget()
    {
        var (client, transport, delay) = await LoggedInAsync();
        transport.After = (request, _, response) =>
            request.Url.AbsolutePath.EndsWith(Grant, StringComparison.Ordinal) ? throw new TransportException("down") : response;

        var ex = await Should.ThrowAsync<GameServerException>(() => GrantAsync(client, 1));
        ex.Error.Code.ShouldBe(ErrorCodes.NetworkError);
        transport.RequestsTo(Grant).Count().ShouldBe(5);
        delay.Delays.Count.ShouldBe(4);

        // Every attempt reached the server with the same request id: still one effect.
        _harness.Host.Store.All("receipts").Count(r => r.Content["operation"]!.GetValue<string>() == "cheat.grant").ShouldBe(1);
    }

    [Fact]
    public async Task A_stale_local_revision_is_overtaken_by_full_updates()
    {
        var shared = new InMemorySessionStore();
        var (a, _, _) = _harness.NewClient(shared);
        var (localId, secret) = SdkHarness.NewDevice();
        await a.LoginGuestAsync(localId, secret);
        var (b, _, _) = _harness.NewClient(shared);

        await GrantAsync(b, 5);   // wallet rev 1, unknown to client A
        await GrantAsync(a, 7);   // A applies baseRev 1 → newRev 2 (full)

        a.Sections.RevOf("wallet").ShouldBe(2);
        Gold(a).ShouldBe(12);
    }

    [Fact]
    public async Task A_partial_update_that_does_not_follow_the_local_revision_triggers_a_refetch()
    {
        var (client, transport, _) = await LoggedInAsync();
        transport.After = (request, _, response) =>
        {
            if (!request.Url.AbsolutePath.EndsWith(Grant, StringComparison.Ordinal) || response.Status != 200)
            {
                return response;
            }

            var command = SdkCore.CommandResponse.Parser.ParseFrom(response.Body);
            foreach (var update in command.Updates)
            {
                update.Full = false;
                update.BaseRev = 41;
                update.NewRev = 42;
            }

            return new TransportResponse(200, response.ContentType, command.ToByteArray());
        };

        await GrantAsync(client, 3);

        var refetch = transport.RequestsTo(GameServerClient.SnapshotRoute).ShouldHaveSingleItem();
        SdkCore.SnapshotRequest.Parser.ParseFrom(refetch.Body).Sections.ShouldBe(["wallet"]);
        client.Sections.RevOf("wallet").ShouldBe(1);
        Gold(client).ShouldBe(3);
    }

    [Fact]
    public async Task Session_replaced_clears_the_token_raises_the_event_and_never_logs_in_again()
    {
        var (a, transportA, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await a.LoginGuestAsync(localId, secret);
        var replaced = new List<GameServerError>();
        a.SessionReplaced += replaced.Add;

        var (b, _, _) = _harness.NewClient();
        await b.LoginGuestAsync(localId, secret);

        var ex = await Should.ThrowAsync<GameServerException>(() => GrantAsync(a, 1));
        ex.Error.Code.ShouldBe(ErrorCodes.SessionReplaced);
        replaced.ShouldHaveSingleItem();
        a.HasSession.ShouldBeFalse();
        transportA.RequestsTo(GameServerClient.LoginRoute).Count().ShouldBe(1);
        transportA.RequestsTo(Grant).Count().ShouldBe(1);

        (await Should.ThrowAsync<GameServerException>(() => GrantAsync(a, 1))).Error.Code.ShouldBe(ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Gate_and_account_errors_raise_events_without_retries()
    {
        var (client, transport, delay) = await LoggedInAsync();
        var maintenance = 0;
        var update = 0;
        var blocked = new List<string>();
        client.Maintenance += _ => maintenance++;
        client.UpdateRequired += _ => update++;
        client.AccountBlocked += e => blocked.Add(e.Code);

        // The gate covers Player endpoints (the Dev cheat endpoint is not gated).
        Task<CommandResult<SampleGame.Protocol.V1.BuyItemResult>> BuyAsync() => client.SendCommandAsync(
            Buy,
            new SampleGame.Protocol.V1.BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 },
            SampleGame.Protocol.V1.BuyItemResult.Parser);

        _harness.Gate.Decision = GateDecision.Maintenance;
        (await Should.ThrowAsync<GameServerException>(BuyAsync)).Error.Code.ShouldBe(ErrorCodes.Maintenance);
        _harness.Gate.Decision = GateDecision.UpdateRequired;
        (await Should.ThrowAsync<GameServerException>(BuyAsync)).Error.HttpStatus.ShouldBe(426);
        _harness.Gate.Decision = GateDecision.Allow;

        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index == start ? ScriptedTransport.Error(403, ErrorCodes.AccountSuspended, false) : null;
        await Should.ThrowAsync<GameServerException>(() => GrantAsync(client, 1));

        maintenance.ShouldBe(1);
        update.ShouldBe(1);
        blocked.ShouldBe([ErrorCodes.AccountSuspended]);
        client.HasSession.ShouldBeTrue();
        delay.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Config_version_is_tracked_and_refreshed_through_get_config()
    {
        var (client, _, _) = _harness.NewClient();
        var announced = new List<string>();
        client.ConfigVersionChanged += announced.Add;
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);

        announced.ShouldHaveSingleItem().ShouldStartWith("test-1+");
        client.ConfigOutdated.ShouldBeTrue();

        (await client.RefreshConfigAsync()).ShouldBeTrue();
        client.Config.Version.ShouldBe(announced[0]);
        client.Config.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(["battle.json", "counter.json", "resources.json", "shop.json"]);
        client.ConfigOutdated.ShouldBeFalse();

        (await client.RefreshConfigAsync()).ShouldBeFalse();
        await GrantAsync(client, 1);
        announced.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Time_sync_works_without_a_session()
    {
        var (client, _, _) = _harness.NewClient();
        _harness.Host.Clock.Advance(TimeSpan.FromDays(3));
        var now = await client.SyncTimeAsync();
        (now - _harness.Host.Clock.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Section_store_never_goes_back_in_time()
    {
        var store = new SectionStore();
        store.Set(new SdkCore.Section { Id = "wallet", Rev = 5, Payload = ByteString.CopyFromUtf8("new") });
        store.Apply(new SdkCore.SectionUpdate { Id = "wallet", BaseRev = 3, NewRev = 4, Full = true, Payload = ByteString.CopyFromUtf8("old") }).ShouldBeTrue();
        store.RevOf("wallet").ShouldBe(5);
        store.Apply(new SdkCore.SectionUpdate { Id = "wallet", BaseRev = 5, NewRev = 6, Full = false, Payload = ByteString.CopyFromUtf8("next") }).ShouldBeTrue();
        store.RevOf("wallet").ShouldBe(6);
        store.Apply(new SdkCore.SectionUpdate { Id = "wallet", BaseRev = 6, NewRev = 8, Full = false }).ShouldBeFalse();
    }
}
