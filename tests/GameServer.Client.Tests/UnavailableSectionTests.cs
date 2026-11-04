extern alias sdk;

using Google.Protobuf;
using sdk::GameServer.Client;
using Shouldly;
using Economy = sdk::GameServer.Modules.Economy.Protocol.V1;
using SdkCore = sdk::GameServer.Protocol.Core.V1;
using SdkIdentity = sdk::GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T045 (FR-027, BC-8, research R12): the SDK marks the sections an answer lists as unavailable, keeps their last good copy,
/// clears the mark on a later read and raises an event per change; a same-player login keeps the copies, another player's login
/// clears them; a version-ahead envelope is retried with the same bytes and request id; an answer without the field changes nothing.
/// </summary>
public sealed class UnavailableSectionTests : IAsyncLifetime
{
    private const string Wallet = "wallet";
    private const string Grant = "api/v1/cheat/grant";

    private static readonly SdkCore.UnavailableSection WalletAhead = new() { Id = Wallet, Code = ErrorCodes.AggregateVersionAhead, CodeNumber = 24 };

    private SdkHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await SdkHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public void The_store_marks_keeps_the_last_copy_clears_on_a_later_read_and_raises_one_event_per_change()
    {
        var store = new SectionStore();
        var events = new List<string>();
        store.AvailabilityChanged += events.Add;
        store.Set(new SdkCore.Section { Id = Wallet, Rev = 3, Payload = ByteString.CopyFromUtf8("w3") });

        store.MarkUnavailable([WalletAhead]);
        store.MarkUnavailable([WalletAhead]);

        store.IsUnavailable(Wallet).ShouldBeTrue();
        store.Unavailable[Wallet].Code.ShouldBe(ErrorCodes.AggregateVersionAhead);
        store.Unavailable[Wallet].CodeNumber.ShouldBe(24);
        store.RevOf(Wallet).ShouldBe(3, "the last good copy stays");
        events.ShouldBe([Wallet]);

        store.Set(new SdkCore.Section { Id = Wallet, Rev = 4, Payload = ByteString.CopyFromUtf8("w4") });
        store.IsUnavailable(Wallet).ShouldBeFalse();
        events.ShouldBe([Wallet, Wallet]);

        store.MarkUnavailable([WalletAhead]);
        store.Apply(new SdkCore.SectionUpdate { Id = Wallet, BaseRev = 4, NewRev = 5, Payload = ByteString.CopyFromUtf8("w5"), Full = true }).ShouldBeTrue();
        store.IsUnavailable(Wallet).ShouldBeFalse();

        store.MarkUnavailable([WalletAhead]);
        store.Clear();
        store.Unavailable.ShouldBeEmpty();
        events.Count.ShouldBe(5, "mark, clear by Set, mark, clear by Apply, mark; Clear raises nothing");
    }

    [Fact]
    public async Task A_same_player_login_keeps_the_last_copy_of_an_unavailable_section_and_another_players_login_clears_it()
    {
        var (client, transport, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var walletRev = client.Sections.RevOf(Wallet);
        client.Sections.TryGet(Wallet, out var before).ShouldBeTrue();
        var withoutWallet = false;
        transport.After = (request, _, response) =>
        {
            if (!withoutWallet || request.Url.AbsolutePath.TrimStart('/') != GameServerClient.LoginRoute)
            {
                return response;
            }

            // What a server returns when it cannot read the wallet (FG-029): the section left out and listed.
            var login = SdkIdentity.LoginResponse.Parser.ParseFrom(response.Body);
            var kept = login.Sections.Where(s => s.Id != Wallet).ToArray();
            login.Sections.Clear();
            login.Sections.Add(kept);
            login.UnavailableSections.Add(WalletAhead);
            return new TransportResponse(response.Status, response.ContentType, login.ToByteArray());
        };

        withoutWallet = true;
        await client.LoginGuestAsync(localId, secret);

        client.Sections.IsUnavailable(Wallet).ShouldBeTrue();
        client.Sections.TryGet(Wallet, out var after).ShouldBeTrue();
        after.Rev.ShouldBe(walletRev);
        after.Payload.ShouldBe(before.Payload);

        var (otherId, otherSecret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(otherId, otherSecret);

        client.Sections.TryGet(Wallet, out _).ShouldBeFalse("another player's login starts from an empty store");
        client.Sections.IsUnavailable(Wallet).ShouldBeTrue("that login listed the wallet too");
    }

    [Fact]
    public async Task Snapshot_and_command_answers_mark_their_unavailable_sections()
    {
        var (client, transport, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        transport.After = (request, _, response) =>
        {
            var route = request.Url.AbsolutePath.TrimStart('/');
            if (route == GameServerClient.SnapshotRoute)
            {
                var snapshot = SdkCore.SnapshotResponse.Parser.ParseFrom(response.Body);
                snapshot.UnavailableSections.Add(new SdkCore.UnavailableSection { Id = "profile", Code = ErrorCodes.AggregateUnreadable, CodeNumber = 25 });
                return new TransportResponse(response.Status, response.ContentType, snapshot.ToByteArray());
            }

            if (route == Grant)
            {
                var command = SdkCore.CommandResponse.Parser.ParseFrom(response.Body);
                command.UnavailableSections.Add(WalletAhead);
                command.Updates.Clear();
                return new TransportResponse(response.Status, response.ContentType, command.ToByteArray());
            }

            return response;
        };

        await client.RefreshSectionsAsync(["counter"]);
        client.Sections.Unavailable["profile"].Code.ShouldBe(ErrorCodes.AggregateUnreadable);

        var result = await client.SendCommandAsync(Grant, new Economy.CheatGrantRequest { Resource = "gold", Quantity = 1 }, Economy.CheatGrantResult.Parser);

        result.IsOk.ShouldBeTrue();
        client.Sections.IsUnavailable(Wallet).ShouldBeTrue();
    }

    [Fact]
    public async Task A_version_ahead_envelope_is_retried_with_the_same_bytes_and_request_id()
    {
        var (client, transport, delay) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var start = transport.Exchanges.Count;
        transport.Before = (_, index) => index == start ? ScriptedTransport.Error(503, ErrorCodes.AggregateVersionAhead, true, retryAfterMs: 1000) : null;

        var result = await client.SendCommandAsync(Grant, new Economy.CheatGrantRequest { Resource = "gold", Quantity = 1 }, Economy.CheatGrantResult.Parser);

        result.IsOk.ShouldBeTrue();
        var attempts = transport.Exchanges.Skip(start).Select(e => e.Request).ToArray();
        attempts.Length.ShouldBe(2);
        attempts[1].Body.ShouldBe(attempts[0].Body);
        attempts[1].Headers[GameServerClient.RequestIdHeader].ShouldBe(attempts[0].Headers[GameServerClient.RequestIdHeader]);
        delay.Delays.ShouldHaveSingleItem().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task An_answer_without_the_field_changes_nothing()
    {
        var (client, _, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        var events = 0;
        client.Sections.AvailabilityChanged += _ => events++;

        await client.LoginGuestAsync(localId, secret);
        await client.RefreshSectionsAsync();

        client.Sections.Unavailable.ShouldBeEmpty();
        events.ShouldBe(0);
        client.Sections.RevOf("profile").ShouldBe(1);
    }
}
