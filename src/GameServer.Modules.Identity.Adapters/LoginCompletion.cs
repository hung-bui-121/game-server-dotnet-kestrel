using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Limits;
using GameServer.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

public sealed record LoginResult(
    string Token,
    string PlayerId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<LoadedAggregate> Sections);

/// <summary>
/// Per-IP login limits shared by guest and provider logins (SEC-13): failed logins per IP are blocked, request bursts per
/// IP only raise a warning metric (carrier-grade NAT puts many players behind one IP).
/// </summary>
public sealed class LoginRateLimits
{
    private readonly FixedWindowCounter _failuresPerIp;
    private readonly FixedWindowCounter _requestsPerIp;
    private readonly IdentityMetrics _metrics;

    public LoginRateLimits(IClock clock, IOptions<IdentityOptions> options, IdentityMetrics metrics)
    {
        var minute = TimeSpan.FromMinutes(1);
        _failuresPerIp = new FixedWindowCounter(clock, minute, options.Value.FailedLoginPerIpPerMinute);
        _requestsPerIp = new FixedWindowCounter(clock, minute, options.Value.LoginPerIpWarnPerMinute);
        _metrics = metrics;
    }

    /// <summary>Throws RATE_LIMITED when the IP failed too often; counts the request for the burst warning.</summary>
    public void Enter(string clientIp)
    {
        if (_failuresPerIp.IsOver(clientIp))
        {
            throw RateLimited(_failuresPerIp.RetryAfterSeconds(clientIp));
        }

        if (_requestsPerIp.Increment(clientIp) > _requestsPerIp.Limit)
        {
            _metrics.IpBurstWarning();
        }
    }

    public void Failed(string clientIp) => _failuresPerIp.Increment(clientIp);

    public static GameServerException RateLimited(int retryAfterSeconds) =>
        new(CoreErrorCodes.RateLimited, "Login rate limit.") { RetryAfterMs = retryAfterSeconds * 1000 };
}

/// <summary>
/// Common end of every login (IDN-07, IDN-08, IDN-10): account checks → session epoch +1 (single session + takeover)
/// and the mapping key recorded in <c>profile.identities</c> → new session → snapshot.
/// </summary>
public sealed class LoginCompletion(
    IPlayerAggregateWriter writer,
    PlayerSnapshotReader snapshots,
    ProfileCache profiles,
    SessionService sessions,
    IClock clock,
    IdentityMetrics metrics,
    ILogger<LoginCompletion> logger)
{
    public async Task<LoginResult> CompleteAsync(string playerId, string mappingKey, CancellationToken cancellationToken)
    {
        var profile = await profiles.ReadFreshAsync(playerId, cancellationToken);
        if (AccountChecks.CheckAccess(profile, clock.UtcNow) is { } denial)
        {
            metrics.Login(denial.Code.Name);
            throw new GameServerException(denial.Code, $"Login refused for {playerId}.") { Details = denial.Details };
        }

        // Single session + takeover: the new epoch invalidates every older session (IDN-08, IDN-10).
        var (updated, _) = await writer.UpdateAsync<ProfileState>(
            playerId,
            IdentityAggregates.Profile,
            p =>
            {
                // A ban or deletion committed after the read above wins: no token is issued (review m8).
                if (AccountChecks.CheckAccess(p, clock.UtcNow) is { } late)
                {
                    throw new GameServerException(late.Code, $"Login refused for {playerId}.") { Details = late.Details };
                }

                return p with
                {
                    SessionEpoch = p.SessionEpoch + 1,
                    Identities = p.Identities.Contains(mappingKey, StringComparer.Ordinal) ? p.Identities : [.. p.Identities, mappingKey],
                };
            },
            cancellationToken);
        profiles.Set(playerId, updated);

        var (token, expiresAt) = await sessions.CreateAsync(playerId, updated.SessionEpoch, cancellationToken);
        var sections = await snapshots.ReadAsync(playerId, null, cancellationToken);
        metrics.Login("ok");
        logger.LogInformation("Login of player {PlayerId} with {Provider} (epoch {Epoch}).", playerId, ProviderIdentities.ProviderOf(mappingKey), updated.SessionEpoch);
        return new LoginResult(token, playerId, expiresAt, sections);
    }
}
