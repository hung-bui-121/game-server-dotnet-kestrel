extern alias sdk;

using System.Globalization;
using GameServer.Tests.Shared;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 010 US1 (FR-021, D4): <c>RequestAccountDeletionAsync</c> against a real host. Success and ACCOUNT_DELETED both
/// mean the deletion is committed; the SDK clears the session and sections and raises <c>AccountBlocked</c> with
/// <c>purgeAfter</c>.
/// </summary>
public sealed class AccountDeletionClientTests
{
    private static (GameServerClient Client, ScriptedTransport Transport) NewClient(TestHost host)
    {
        var http = host.Client();
        var transport = new ScriptedTransport(new HttpClientTransport(http));
        return (new GameServerClient(new GameServerClientOptions(http.BaseAddress!, "1.0.0", "android", "b1"), transport, delay: new RecordingDelay()), transport);
    }

    private static string PurgeAfter(TestHost host) =>
        host.Clock.UtcNow.AddDays(30).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Ok_clears_the_session_and_sections_and_raises_account_blocked_with_the_purge_date()
    {
        await using var host = await TestHost.StartAsync();
        var (client, _) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        client.Sections.TryGet("profile", out _).ShouldBeTrue();
        GameServerError? blocked = null;
        client.AccountBlocked += e => blocked = e;

        var result = await client.RequestAccountDeletionAsync();

        result.IsOk.ShouldBeTrue();
        result.Result!.PurgeAfterUtcMs.ShouldBe(host.Clock.UtcNow.AddDays(30).ToUnixTimeMilliseconds());
        client.HasSession.ShouldBeFalse();
        client.Sections.TryGet("profile", out _).ShouldBeFalse();
        blocked.ShouldNotBeNull();
        blocked.Code.ShouldBe(ErrorCodes.AccountDeleted);
        blocked.Details[GameServerClient.PurgeAfterDetail].ShouldBe(PurgeAfter(host));

        // Logging in again tells the game the same date (US1-3).
        var login = await Should.ThrowAsync<GameServerException>(() => client.LoginGuestAsync(localId, secret));
        login.Error.Code.ShouldBe(ErrorCodes.AccountDeleted);
        login.Error.Details[GameServerClient.PurgeAfterDetail].ShouldBe(PurgeAfter(host));
    }

    [Fact]
    public async Task A_lost_response_is_retried_and_account_deleted_means_committed()
    {
        await using var host = await TestHost.StartAsync();
        var (client, transport) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var lost = false;
        transport.After = (request, _, response) =>
        {
            if (!lost && request.Url.AbsolutePath.TrimStart('/') == GameServerClient.DeleteAccountRoute)
            {
                lost = true;
                throw new TransportException("response lost");
            }

            return response;
        };
        GameServerError? blocked = null;
        client.AccountBlocked += e => blocked = e;

        var ex = await Should.ThrowAsync<GameServerException>(() => client.RequestAccountDeletionAsync());

        ex.Error.Code.ShouldBe(ErrorCodes.AccountDeleted);
        ex.Error.Details[GameServerClient.PurgeAfterDetail].ShouldBe(PurgeAfter(host));
        blocked.ShouldNotBeNull();
        client.HasSession.ShouldBeFalse();
        client.Sections.TryGet("profile", out _).ShouldBeFalse();

        // Both attempts carried the same request id and bytes (SDK-02).
        var attempts = transport.RequestsTo(GameServerClient.DeleteAccountRoute).ToArray();
        attempts.Length.ShouldBe(2);
        attempts[1].Headers[GameServerClient.RequestIdHeader].ShouldBe(attempts[0].Headers[GameServerClient.RequestIdHeader]);
        attempts[1].Body.ShouldBe(attempts[0].Body);
    }
}
