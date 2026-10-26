using System.Net;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US3 of 002: deleted → suspended → replaced → unauthenticated (IDN-10, BAN-01 P0 part).</summary>
public sealed class AccountStateTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task SetProfileAsync(string playerId, Func<ProfileState, ProfileState> mutate) =>
        _host.Services.GetRequiredService<IPlayerAggregateWriter>().UpdateAsync(playerId, IdentityAggregates.Profile, mutate, CancellationToken.None);

    private Task<HttpResponseMessage> SnapshotAsync(string token) =>
        _host.BearerClient(token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

    [Fact]
    public async Task Login_ban_blocks_login_and_existing_sessions_with_until()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var old = await _host.Client().LoginAsync(localId, secret);
        var current = await _host.Client().LoginAsync(localId, secret);
        var until = _host.Clock.UtcNow.AddDays(3);
        await SetProfileAsync(current.PlayerId, p => p with { Ban = new BanInfo { Reason = "cheat", UntilUtc = until, Scope = [BanInfo.LoginScope] } });

        var login = await _host.Client().PostLoginAsync(localId, secret);
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var loginError = await login.ReadErrorAsync();
        loginError.Code.ShouldBe("ACCOUNT_SUSPENDED");
        loginError.Details["until"].ShouldBe(until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

        (await (await SnapshotAsync(current.SessionToken)).ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");

        // Suspension has priority over SESSION_REPLACED (IDN-10).
        (await (await SnapshotAsync(old.SessionToken)).ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");
    }

    [Fact]
    public async Task Expired_ban_no_longer_blocks()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await _host.Client().LoginAsync(localId, secret);
        await SetProfileAsync(login.PlayerId, p => p with { Ban = new BanInfo { UntilUtc = _host.Clock.UtcNow.AddHours(1) } });

        _host.Clock.Advance(TimeSpan.FromHours(2));

        (await _host.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Group_ban_blocks_only_that_command_group_and_keeps_the_session()
    {
        var login = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await SetProfileAsync(login.PlayerId, p => p with { Ban = new BanInfo { Scope = ["counter"], UntilUtc = null } });
        var player = _host.BearerClient(login.SessionToken);

        var command = await player.PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());

        command.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await command.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");
        (await SnapshotAsync(login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleted_account_has_the_highest_priority()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await _host.Client().LoginAsync(localId, secret);
        await SetProfileAsync(login.PlayerId, p => p with
        {
            Deleted = new DeletionInfo { RequestedAtUtc = _host.Clock.UtcNow, PurgeAfterUtc = _host.Clock.UtcNow.AddDays(30) },
            Ban = new BanInfo { Scope = [BanInfo.LoginScope] },
        });

        var relogin = await _host.Client().PostLoginAsync(localId, secret);
        relogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await relogin.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
        (await (await SnapshotAsync(login.SessionToken)).ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
    }
}
