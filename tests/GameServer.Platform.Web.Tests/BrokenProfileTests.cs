using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T040 (FR-021, FR-023, US3-6, VERIFY V5 fallback): a profile that cannot be read refuses every authenticated request and
/// every provider login with its typed code (never INTERNAL), with the retry delay for a version-ahead profile; a broken profile is
/// never cached, so repairing the document makes the next request succeed.
/// </summary>
public sealed class BrokenProfileTests
{
    private const string Snapshot = "/api/v1/player/snapshot";

    /// <summary>Past the profile cache (Identity:CacheSeconds, 60 s by default), so the next request reads the store.</summary>
    private static readonly TimeSpan PastProfileCache = TimeSpan.FromSeconds(61);

    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "AGGREGATE_VERSION_AHEAD", 1000)]
    [InlineData(false, HttpStatusCode.InternalServerError, "AGGREGATE_UNREADABLE", 0)]
    public async Task Bearer_requests_for_a_broken_profile_answer_the_typed_code_and_succeed_once_repaired(bool versionAhead, HttpStatusCode status, string code, int retryAfterMs)
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var bearer = host.BearerClient(login.SessionToken);
        var original = Break(host, login.PlayerId, versionAhead);
        host.Clock.Advance(PastProfileCache);

        var snapshot = await bearer.PostProtobufAsync(Snapshot, new SnapshotRequest());
        var command = await bearer.PostProtobufAsync(BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(1), ProtobufHttp.NewRequestId());

        foreach (var response in new[] { snapshot, command })
        {
            response.StatusCode.ShouldBe(status);
            var error = await response.ReadErrorAsync();
            error.Code.ShouldBe(code);
            error.RetryAfterMs.ShouldBe(retryAfterMs);
            error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = IdentityAggregates.Profile });
        }

        host.LogEntries.Count(e => e.Category == AggregateReadFailures.LogCategory).ShouldBe(2, "one line per failed read");
        host.LogEntries.ShouldNotContain(e => e.Message == "Unhandled exception.");

        // Nothing was cached: the repaired document serves the next request.
        host.Store.Seed(ProfileKey(login.PlayerId), original);
        (await bearer.PostProtobufAsync(Snapshot, new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_provider_login_for_a_broken_profile_answers_the_typed_code()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var credential = new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + "g" + Guid.NewGuid().ToString("N")[..20] } };
        var first = await host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var playerId = (await first.ReadProtobufAsync<LoginResponse>()).PlayerId;
        Break(host, playerId, versionAhead: true);

        var refused = await host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential });

        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = await refused.ReadErrorAsync();
        error.Code.ShouldBe("AGGREGATE_VERSION_AHEAD");
        error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = IdentityAggregates.Profile });
    }

    private static DocumentKey ProfileKey(string playerId) => new(IdentityAggregates.ProfileDescriptor.Collection, playerId);

    /// <summary>Damages the stored profile and returns the undamaged copy.</summary>
    private static JsonObject Break(TestHost host, string playerId, bool versionAhead)
    {
        var key = ProfileKey(playerId);
        var original = (JsonObject)host.Store.Peek(key)!.DeepClone();
        var damaged = (JsonObject)original.DeepClone();
        if (versionAhead)
        {
            damaged["schemaVersion"] = IdentityAggregates.ProfileDescriptor.SchemaVersion + 1;
        }
        else
        {
            damaged["state"] = "not an object";
        }

        host.Store.Seed(key, damaged);
        return original;
    }
}
