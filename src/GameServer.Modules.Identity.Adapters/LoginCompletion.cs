using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Limits;
using GameServer.Platform.Persistence;
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
/// and the mapping key recorded in <c>profile.identities</c> → new session → snapshot. A first login creates the profile
/// anchored on the identity mapping (<c>identities/{mappingKey}</c>, research R9): when the account purge removed the
/// mapping meanwhile, nothing is created and the login answers ACCOUNT_DELETED.
/// </summary>
public sealed class LoginCompletion(
    IPlayerAggregateWriter writer,
    PlayerSnapshotReader snapshots,
    ProfileCache profiles,
    SessionService sessions,
    IClock clock,
    IdentityMetrics metrics,
    LoginAnalytics analytics,
    ILogger<LoginCompletion> logger)
{
    /// <summary>
    /// Test seam: awaited after the caller read the identity mapping and before the profile is read and written, so a test
    /// can complete an account purge in between (US2-5). Null in production.
    /// </summary>
    internal Func<string, Task>? AfterMappingRead { get; set; }

    public async Task<LoginResult> CompleteAsync(string playerId, string mappingKey, CancellationToken cancellationToken)
    {
        if (AfterMappingRead is { } pause)
        {
            await pause(playerId);
        }

        // Null = no profile yet (first login) or purged; the creation anchor below tells them apart.
        var profile = await profiles.ReadFreshAsync(playerId, cancellationToken);
        if (profile is not null && AccountChecks.CheckAccess(profile, clock.UtcNow) is { } denial)
        {
            metrics.Login(denial.Code.Name);
            throw new GameServerException(denial.Code, $"Login refused for {playerId}.") { Details = denial.Details };
        }

        // Single session + takeover: the new epoch invalidates every older session (IDN-08, IDN-10).
        var updated = await WriteEpochAsync(playerId, mappingKey, cancellationToken);
        profiles.Set(playerId, updated);

        var (token, expiresAt) = await sessions.CreateAsync(playerId, updated.SessionEpoch, cancellationToken);
        var sections = await snapshots.ReadAsync(playerId, null, cancellationToken);
        metrics.Login("ok");
        logger.LogInformation("Login of player {PlayerId} with {Provider} (epoch {Epoch}).", playerId, ProviderIdentities.ProviderOf(mappingKey), updated.SessionEpoch);

        // Last step that can fail is done: one login record (FR-015); refused or failed logins threw earlier (FR-016).
        analytics.LoggedIn(playerId, ProviderIdentities.ProviderOf(mappingKey), createdPlayer: profile is null);
        return new LoginResult(token, playerId, expiresAt, sections);
    }

    private async Task<ProfileState> WriteEpochAsync(string playerId, string mappingKey, CancellationToken cancellationToken)
    {
        try
        {
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
                new DocumentKey(IdentityCollections.Identities, mappingKey),
                cancellationToken);
            return updated;
        }
        catch (AggregateAnchorMissingException)
        {
            // The mapping was removed (account purged) between the mapping read and this transaction (research R9).
            metrics.Login(CoreErrorCodes.AccountDeleted.Name);
            throw new GameServerException(CoreErrorCodes.AccountDeleted, $"Login refused for {playerId}: the account is purged.");
        }
    }
}
