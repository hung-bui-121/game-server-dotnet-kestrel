using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Protocol.V1;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 010 US1: a player requests account deletion (PRIV-01, FR-006..FR-010) on two instances sharing one store and
/// clock, each with the production profile/session cache (60 s), then every credential and token is refused.
/// </summary>
public sealed class AccountDeletionTests : IAsyncLifetime
{
    private const string SnapshotRoute = "/api/v1/player/snapshot";
    private const string IncrementRoute = "/api/v1/counter/increment";

    private TestHost _a = null!;
    private TestHost _b = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await TestHost.StartAsync(settings: WithCache);
        _b = await TestHost.StartAsync(settings: WithCache, store: _a.Store, clock: _a.Clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static void WithCache(Dictionary<string, string?> settings) => settings["Identity:CacheSeconds"] = "60";

    private static Task<HttpResponseMessage> RequestDeletionAsync(HttpClient client, string? requestId = null) =>
        client.PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), requestId ?? ProtobufHttp.NewRequestId());

    private static Task<HttpResponseMessage> SnapshotAsync(HttpClient client) => client.PostProtobufAsync(SnapshotRoute, new SnapshotRequest());

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static string NewPgsId() => "g" + Guid.NewGuid().ToString("N")[..20];

    private static Task<HttpResponseMessage> ProviderLoginAsync(HttpClient client, ProviderCredential credential) =>
        client.PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential });

    private string PurgeAfter => _a.Clock.UtcNow.AddDays(30).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private ProfileState StoredProfile(string playerId)
    {
        var loaded = _a.Services.GetRequiredService<PlayerSnapshotReader>()
            .ReadOneAsync(playerId, IdentityAggregates.Profile, CancellationToken.None).GetAwaiter().GetResult();
        return (ProfileState)loaded.State;
    }

    private int Receipts(string playerId, string operation) =>
        _a.Store.All(ReceiptDocument.Collection).Count(d =>
            d.Content["playerId"]?.GetValue<string>() == playerId && d.Content["operation"]?.GetValue<string>() == operation);

    private int Sessions(string playerId) =>
        _a.Store.All(SessionService.Collection).Count(d => d.Content["playerId"]?.GetValue<string>() == playerId);

    private async Task<LoginResponse> DeletedGuestAsync(string localId, byte[] secret)
    {
        var login = await _a.Client().LoginAsync(localId, secret);
        (await (await RequestDeletionAsync(_a.BearerClient(login.SessionToken))).ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        return login;
    }

    private static async Task ShouldBeDeletedAsync(HttpResponseMessage response, string purgeAfter)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe("ACCOUNT_DELETED");
        error.Details["purgeAfter"].ShouldBe(purgeAfter);
    }

    [Fact]
    public async Task Request_locks_the_account_in_one_transaction_with_one_receipt()
    {
        var login = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var epoch = StoredProfile(login.PlayerId).SessionEpoch;
        var now = _a.Clock.UtcNow;

        var response = await RequestDeletionAsync(_a.BearerClient(login.SessionToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var command = await response.ReadProtobufAsync<CommandResponse>();
        command.Code.ShouldBe("OK");
        command.ReceiptId.ShouldNotBeNullOrEmpty();
        var result = RequestAccountDeletionResult.Parser.ParseFrom(command.Result);
        result.RequestedAtUtcMs.ShouldBe(now.ToUnixTimeMilliseconds());
        result.PurgeAfterUtcMs.ShouldBe(now.AddDays(30).ToUnixTimeMilliseconds());
        command.Updates.Single().Id.ShouldBe(IdentityAggregates.Profile);

        var profile = StoredProfile(login.PlayerId);
        profile.Deleted.ShouldNotBeNull();
        profile.Deleted.RequestedAtUtc.ShouldBe(now);
        profile.Deleted.PurgeAfterUtc.ShouldBe(now.AddDays(30));
        profile.Deleted.PurgeStartedAtUtc.ShouldBeNull();
        profile.SessionEpoch.ShouldBe(epoch + 1);
        Receipts(login.PlayerId, RequestDeletionHandler.Operation).ShouldBe(1);
    }

    [Fact]
    public async Task Old_tokens_are_refused_at_once_on_the_serving_instance_and_within_60_seconds_elsewhere()
    {
        var login = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var onA = _a.BearerClient(login.SessionToken);
        var onB = _b.BearerClient(login.SessionToken);
        (await SnapshotAsync(onB)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RequestDeletionAsync(onA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var purgeAfter = PurgeAfter;

        await ShouldBeDeletedAsync(await SnapshotAsync(onA), purgeAfter);
        await ShouldBeDeletedAsync(await onA.PostProtobufAsync(IncrementRoute, new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId()), purgeAfter);

        // Instance B still has the profile cached (IDN-08 window); after it expires every request is refused (FR-007, FR-009).
        (await SnapshotAsync(onB)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _a.Clock.Advance(TimeSpan.FromSeconds(61));
        await ShouldBeDeletedAsync(await SnapshotAsync(onB), purgeAfter);
        await ShouldBeDeletedAsync(await onB.PostProtobufAsync(IncrementRoute, new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId()), purgeAfter);
    }

    [Fact]
    public async Task A_retry_is_replayed_or_answered_account_deleted_and_never_writes_a_second_receipt()
    {
        var login = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var onB = _b.BearerClient(login.SessionToken);
        (await SnapshotAsync(onB)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = ProtobufHttp.NewRequestId();

        (await RequestDeletionAsync(_a.BearerClient(login.SessionToken), requestId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Serving instance: the refreshed cache answers before the runner.
        await ShouldBeDeletedAsync(await RequestDeletionAsync(_a.BearerClient(login.SessionToken), requestId), PurgeAfter);

        // Other instance with a stale cache: the same request id replays the committed receipt.
        var replay = await (await RequestDeletionAsync(onB, requestId)).ReadProtobufAsync<CommandResponse>();
        replay.Code.ShouldBe("OK");
        replay.Replayed.ShouldBeTrue();

        // A new request id there reaches the handler, which refuses without a receipt.
        await ShouldBeDeletedAsync(await RequestDeletionAsync(onB), PurgeAfter);
        Receipts(login.PlayerId, RequestDeletionHandler.Operation).ShouldBe(1);
    }

    [Fact]
    public async Task Command_group_bans_do_not_block_the_request_but_a_login_ban_does()
    {
        var writer = _a.Services.GetRequiredService<IPlayerAggregateWriter>();
        var grouped = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var suspended = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await writer.UpdateAsync<ProfileState>(grouped.PlayerId, IdentityAggregates.Profile, p => p with { Ban = new BanInfo { Scope = ["identity", "counter"] } }, CancellationToken.None);
        await writer.UpdateAsync<ProfileState>(suspended.PlayerId, IdentityAggregates.Profile, p => p with { Ban = new BanInfo { Scope = [BanInfo.LoginScope] } }, CancellationToken.None);
        _a.Clock.Advance(TimeSpan.FromSeconds(61));

        var name = await _a.BearerClient(grouped.SessionToken).PostProtobufAsync(
            IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "Banned Name" }, ProtobufHttp.NewRequestId());
        (await name.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");
        var allowed = await RequestDeletionAsync(_a.BearerClient(grouped.SessionToken));
        (await allowed.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");

        var refused = await RequestDeletionAsync(_a.BearerClient(suspended.SessionToken));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");
        StoredProfile(suspended.PlayerId).Deleted.ShouldBeNull();
    }

    [Fact]
    public async Task A_committed_request_increments_the_request_metric_and_logs_only_the_player_id()
    {
        var requests = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == IdentityMetrics.MeterName && instrument.Name == "account_deletion_requests_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => requests.Enqueue(value));
        listener.Start();
        var login = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        (await RequestDeletionAsync(_a.BearerClient(login.SessionToken))).StatusCode.ShouldBe(HttpStatusCode.OK);

        requests.Sum().ShouldBeGreaterThanOrEqualTo(1);
        var line = _a.Logs.Single(l => l.StartsWith("Account deletion requested", StringComparison.Ordinal) && l.Contains(login.PlayerId, StringComparison.Ordinal));
        line.ShouldNotContain(login.SessionToken);
    }

    [Fact]
    public async Task Guest_login_of_a_pending_account_is_refused_without_session_or_new_player()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await DeletedGuestAsync(localId, secret);
        var sessions = Sessions(login.PlayerId);
        var profiles = _a.Store.Count(IdentityAggregates.ProfileDescriptor.Collection);
        var mapping = _a.Store.Peek(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId)));

        await ShouldBeDeletedAsync(await _a.Client().PostLoginAsync(localId, secret), PurgeAfter);
        await ShouldBeDeletedAsync(await _b.Client().PostLoginAsync(localId, secret), PurgeAfter);

        Sessions(login.PlayerId).ShouldBe(sessions);
        _a.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(profiles);
        JsonNode.DeepEquals(_a.Store.Peek(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId))), mapping).ShouldBeTrue();

        // Without the credential nothing about the account leaks (IDN-02).
        var wrong = await _a.Client().PostLoginAsync(localId, IdentityHttp.NewSecret());
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.ReadErrorAsync()).Code.ShouldBe("UNAUTHENTICATED");
    }

    [Fact]
    public async Task Provider_login_of_a_pending_account_answers_account_deleted_before_switch_account()
    {
        var pgs = NewPgsId();
        var owner = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        (await (await _a.BearerClient(owner.SessionToken).PostProtobufAsync(
            IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) }, ProtobufHttp.NewRequestId())).ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        (await RequestDeletionAsync(_a.BearerClient(owner.SessionToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var players = _a.Store.Count(IdentityAggregates.ProfileDescriptor.Collection);
        var mappings = _a.Store.Count(IdentityCollections.Identities);

        await ShouldBeDeletedAsync(await ProviderLoginAsync(_a.Client(), Pgs(pgs)), PurgeAfter);
        await ShouldBeDeletedAsync(await ProviderLoginAsync(_b.BearerClient(other.SessionToken), Pgs(pgs)), PurgeAfter);

        _a.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(players);
        _a.Store.Count(IdentityCollections.Identities).ShouldBe(mappings);
        Sessions(owner.PlayerId).ShouldBe(1);

        // The identity stays reserved: another player cannot link it during grace (US1-4).
        var link = await _a.BearerClient(other.SessionToken).PostProtobufAsync(
            IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) }, ProtobufHttp.NewRequestId());
        link.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await link.ReadErrorAsync()).Code.ShouldBe("IDENTITY_CONFLICT");
    }

    [Fact]
    public async Task First_login_after_a_crashed_mapping_insert_still_creates_the_player()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var playerId = PlayerIds.New();
        _a.Store.Seed(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId)), Mapping(localId, playerId, secret));

        var login = await _a.Client().LoginAsync(localId, secret);

        login.PlayerId.ShouldBe(playerId);
        StoredProfile(playerId).SessionEpoch.ShouldBe(1);
    }

    [Fact]
    public async Task A_first_login_that_failed_on_one_instance_leaves_no_cached_absence_there()
    {
        // Review M1: instance A reads the not-yet-created profile of a new player, then its first login fails (here: the profile
        // transaction); the client's retry succeeds on B. A must not answer ACCOUNT_DELETED from a cached "no profile".
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        _a.Store.Faults.OnlyWhenWritingCollection = IdentityAggregates.ProfileDescriptor.Collection;
        _a.Store.Faults.Enqueue(GameServer.Platform.Testing.StoreFault.FailBeforeCommit);
        var failed = await _a.Client().PostLoginAsync(localId, secret);
        _a.Store.Faults.OnlyWhenWritingCollection = null;
        failed.IsSuccessStatusCode.ShouldBeFalse();

        var login = await _b.Client().LoginAsync(localId, secret);

        var onA = await SnapshotAsync(_a.BearerClient(login.SessionToken));
        onA.StatusCode.ShouldBe(HttpStatusCode.OK, await onA.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_login_whose_mapping_is_removed_before_the_profile_write_creates_nothing()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var playerId = PlayerIds.New();
        var key = new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId));
        _a.Store.Seed(key, Mapping(localId, playerId, secret));
        var completion = _a.Services.GetRequiredService<LoginCompletion>();
        completion.AfterMappingRead = _ => ((IKeyValueStore)_a.Store).RemoveAsync(key, CancellationToken.None);
        try
        {
            var refused = await _a.Client().PostLoginAsync(localId, secret);

            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await refused.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
            _a.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, playerId)).ShouldBeNull();
            Sessions(playerId).ShouldBe(0);
        }
        finally
        {
            completion.AfterMappingRead = null;
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("91")]
    public async Task A_grace_period_outside_1_to_90_days_fails_startup(string graceDays)
    {
        Exception? failure = null;
        try
        {
            await using var host = await TestHost.StartAsync(settings: s => s["Identity:Deletion:GraceDays"] = graceDays);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        failure.ShouldNotBeNull();
        failure.ToString().ShouldContain("Identity:Deletion:GraceDays must be 1..90");
    }

    private JsonObject Mapping(string localId, string playerId, byte[] secret) => new()
    {
        ["mappingKey"] = GuestCredentials.MappingKey(localId),
        ["playerId"] = playerId,
        ["gameId"] = "test",
        ["environmentId"] = "dev",
        ["linkedAtUtc"] = AggregateDocumentSerializer.FormatTime(_a.Clock.UtcNow),
        ["providerAttributes"] = new JsonObject
        {
            ["secretHash"] = GuestCredentials.Hash(Encoding.UTF8.GetBytes(IdentityOptions.DevelopmentPepper), secret),
        },
    };
}
