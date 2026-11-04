using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
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
    IReadOnlyList<LoadedAggregate> Sections)
{
    /// <summary>True when this login created the player (FG-013); equals the analytics isNewUser.</summary>
    public bool IsNewPlayer { get; init; }

    /// <summary>Client-visible aggregates the login could not read (FG-029), ordered by id; never the profile (its failure fails the login).</summary>
    public IReadOnlyList<AggregateReadFailure> Unavailable { get; init; } = [];
}

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
/// and the mapping key recorded in <c>profile.identities</c> → new session → snapshot. Every login writes the profile through
/// <see cref="IAccountWriter"/>: a first login creates the account anchored on the identity mapping
/// (<c>identities/{mappingKey}</c>, research R9); when the account purge removed the mapping meanwhile, nothing is created
/// and the login answers ACCOUNT_DELETED. Whether this login created the player comes only from the committed write (FG-013).
/// A broken profile fails the login with its typed code before any write; any other broken aggregate is only listed in
/// <see cref="LoginResult.Unavailable"/> (FG-029).
/// </summary>
public sealed class LoginCompletion(
    IAccountWriter accounts,
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

    /// <param name="correlationId">
    /// Correlation id of the login request, stored in the creation receipt of a new account (FG-015); null stores none.
    /// </param>
    public async Task<LoginResult> CompleteAsync(string playerId, string mappingKey, CancellationToken cancellationToken, string? correlationId = null)
    {
        if (AfterMappingRead is { } pause)
        {
            await pause(playerId);
        }

        // Null = no profile yet (first login) or purged; the creation anchor below tells them apart. Only the access check
        // uses this read: whether the login creates the player is decided inside the write (trap T3).
        var profile = await profiles.ReadFreshAsync(playerId, cancellationToken);
        if (profile is not null && AccountChecks.CheckAccess(profile, clock.UtcNow) is { } denial)
        {
            metrics.Login(denial.Code.Name);
            throw new GameServerException(denial.Code, $"Login refused for {playerId}.") { Details = denial.Details };
        }

        // Single session + takeover: the new epoch invalidates every older session (IDN-08, IDN-10).
        var written = await WriteEpochAsync(playerId, mappingKey, correlationId, cancellationToken);
        var updated = written.Profile;
        profiles.Set(playerId, updated);

        var (token, expiresAt) = await sessions.CreateAsync(playerId, updated.SessionEpoch, cancellationToken);
        var snapshot = await snapshots.ReadAvailableAsync(playerId, null, cancellationToken);
        metrics.Login("ok");
        logger.LogInformation("Login of player {PlayerId} with {Provider} (epoch {Epoch}).", playerId, ProviderIdentities.ProviderOf(mappingKey), updated.SessionEpoch);

        // Last step that can fail is done: one login record (FR-015); refused or failed logins threw earlier (FR-016).
        analytics.LoggedIn(playerId, ProviderIdentities.ProviderOf(mappingKey), createdPlayer: written.Created);
        return new LoginResult(token, playerId, expiresAt, snapshot.Sections) { IsNewPlayer = written.Created, Unavailable = snapshot.Unavailable };
    }

    private async Task<AccountWriteResult<ProfileState>> WriteEpochAsync(
        string playerId,
        string mappingKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await accounts.WriteLoginAsync(
                new AccountLoginWrite<ProfileState>(
                    playerId,
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
                    correlationId ?? string.Empty),
                cancellationToken);
        }
        catch (AggregateAnchorMissingException)
        {
            // The mapping was removed (account purged) between the mapping read and this transaction (research R9).
            metrics.Login(CoreErrorCodes.AccountDeleted.Name);
            throw new GameServerException(CoreErrorCodes.AccountDeleted, $"Login refused for {playerId}: the account is purged.");
        }
    }
}
