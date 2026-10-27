using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
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
/// Guest login (IDN-01, IDN-02, IDN-07, IDN-08, SEC-13): rate limits → mapping insert-or-verify → account checks →
/// session epoch +1 → new session → snapshot.
/// </summary>
public sealed class GuestLoginService
{
    public const string IdentitiesCollection = IdentityCollections.Identities;

    private readonly IKeyValueStore _store;
    private readonly IPlayerAggregateWriter _writer;
    private readonly PlayerSnapshotReader _snapshots;
    private readonly ProfileCache _profiles;
    private readonly SessionService _sessions;
    private readonly IClock _clock;
    private readonly GameServerOptions _gameServer;
    private readonly IdentityMetrics _metrics;
    private readonly ILogger<GuestLoginService> _logger;
    private readonly byte[] _pepper;
    private readonly FixedWindowCounter _perLocalId;
    private readonly FixedWindowCounter _failuresPerIp;
    private readonly FixedWindowCounter _requestsPerIp;

    public GuestLoginService(
        IKeyValueStore store,
        IPlayerAggregateWriter writer,
        PlayerSnapshotReader snapshots,
        ProfileCache profiles,
        SessionService sessions,
        IClock clock,
        GameServerOptions gameServer,
        IOptions<IdentityOptions> options,
        IdentityMetrics metrics,
        ILogger<GuestLoginService> logger)
    {
        _store = store;
        _writer = writer;
        _snapshots = snapshots;
        _profiles = profiles;
        _sessions = sessions;
        _clock = clock;
        _gameServer = gameServer;
        _metrics = metrics;
        _logger = logger;
        var settings = options.Value;
        _pepper = settings.PepperBytes(gameServer);
        var minute = TimeSpan.FromMinutes(1);
        _perLocalId = new FixedWindowCounter(clock, minute, settings.LoginPerLocalIdPerMinute);
        _failuresPerIp = new FixedWindowCounter(clock, minute, settings.FailedLoginPerIpPerMinute);
        _requestsPerIp = new FixedWindowCounter(clock, minute, settings.LoginPerIpWarnPerMinute);
    }

    public async Task<LoginResult> LoginAsync(string localId, ReadOnlyMemory<byte> secret, string clientIp, CancellationToken cancellationToken)
    {
        if (!GuestCredentials.IsValidLocalId(localId) || !GuestCredentials.IsValidSecret(secret.Span))
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid guest credential format.");
        }

        EnforceRateLimits(localId, clientIp);

        var mappingKey = GuestCredentials.MappingKey(localId);
        var playerId = await ResolvePlayerAsync(mappingKey, secret, clientIp, cancellationToken);

        var profile = await _profiles.ReadFreshAsync(playerId, cancellationToken);
        if (AccountChecks.CheckAccess(profile, _clock.UtcNow) is { } denial)
        {
            _metrics.Login(denial.Code.Name);
            throw new GameServerException(denial.Code, $"Login refused for {playerId}.") { Details = denial.Details };
        }

        // Single session + takeover: the new epoch invalidates every older session (IDN-08, IDN-10).
        var (updated, _) = await _writer.UpdateAsync<ProfileState>(
            playerId,
            IdentityAggregates.Profile,
            p =>
            {
                // A ban or deletion committed after the read above wins: no token is issued (review m8).
                if (AccountChecks.CheckAccess(p, _clock.UtcNow) is { } late)
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
        _profiles.Set(playerId, updated);

        var (token, expiresAt) = await _sessions.CreateAsync(playerId, updated.SessionEpoch, cancellationToken);
        var sections = await _snapshots.ReadAsync(playerId, null, cancellationToken);
        _metrics.Login("ok");
        _logger.LogInformation("Guest login for player {PlayerId} (epoch {Epoch}).", playerId, updated.SessionEpoch);
        return new LoginResult(token, playerId, expiresAt, sections);
    }

    private void EnforceRateLimits(string localId, string clientIp)
    {
        if (_perLocalId.IsOver(localId))
        {
            throw RateLimited(_perLocalId.RetryAfterSeconds(localId));
        }

        if (_failuresPerIp.IsOver(clientIp))
        {
            throw RateLimited(_failuresPerIp.RetryAfterSeconds(clientIp));
        }

        _perLocalId.Increment(localId);
        if (_requestsPerIp.Increment(clientIp) > _requestsPerIp.Limit)
        {
            // Carrier-grade NAT puts many players behind one IP: warn, never block valid logins (SEC-13).
            _metrics.IpBurstWarning();
        }
    }

    private async Task<string> ResolvePlayerAsync(string mappingKey, ReadOnlyMemory<byte> secret, string clientIp, CancellationToken cancellationToken)
    {
        var key = new DocumentKey(IdentitiesCollection, mappingKey);
        var existing = await _store.GetAsync(key, cancellationToken);
        if (existing is null)
        {
            var playerId = PlayerIds.New();
            var mapping = new JsonObject
            {
                ["mappingKey"] = mappingKey,
                ["playerId"] = playerId,
                ["gameId"] = _gameServer.GameId,
                ["environmentId"] = _gameServer.EnvironmentId,
                ["linkedAtUtc"] = AggregateDocumentSerializer.FormatTime(_clock.UtcNow),
                ["providerAttributes"] = new JsonObject { ["secretHash"] = GuestCredentials.Hash(_pepper, secret.Span) },
            };
            if (await _store.InsertAsync(key, mapping, null, cancellationToken))
            {
                return playerId;
            }

            // Lost the creation race: the winner's mapping decides (IDN-01).
            existing = await _store.GetAsync(key, cancellationToken)
                ?? throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, "Mapping vanished after insert race.") { RetryAfterMs = 200 };
        }

        var storedHash = existing.Content["providerAttributes"]?["secretHash"]?.GetValue<string>() ?? string.Empty;
        if (!GuestCredentials.Verify(storedHash, _pepper, secret.Span))
        {
            _failuresPerIp.Increment(clientIp);
            _metrics.Login("bad_credentials");
            throw new GameServerException(CoreErrorCodes.Unauthenticated, "Guest secret mismatch.");
        }

        return existing.Content["playerId"]!.GetValue<string>();
    }

    private static GameServerException RateLimited(int retryAfterSeconds) =>
        new(CoreErrorCodes.RateLimited, "Login rate limit.") { RetryAfterMs = retryAfterSeconds * 1000 };
}
