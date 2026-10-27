using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Limits;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Guest login (IDN-01, IDN-02, IDN-07, IDN-08, SEC-13): rate limits → mapping insert-or-verify → account checks →
/// session epoch +1 → new session → snapshot.
/// </summary>
public sealed class GuestLoginService
{
    public const string IdentitiesCollection = IdentityCollections.Identities;

    private readonly IKeyValueStore _store;
    private readonly LoginCompletion _completion;
    private readonly LoginRateLimits _limits;
    private readonly IClock _clock;
    private readonly GameServerOptions _gameServer;
    private readonly IdentityMetrics _metrics;
    private readonly byte[] _pepper;
    private readonly FixedWindowCounter _perLocalId;

    public GuestLoginService(
        IKeyValueStore store,
        LoginCompletion completion,
        LoginRateLimits limits,
        IClock clock,
        GameServerOptions gameServer,
        IOptions<IdentityOptions> options,
        IdentityMetrics metrics)
    {
        _store = store;
        _completion = completion;
        _limits = limits;
        _clock = clock;
        _gameServer = gameServer;
        _metrics = metrics;
        var settings = options.Value;
        _pepper = settings.PepperBytes(gameServer);
        _perLocalId = new FixedWindowCounter(clock, TimeSpan.FromMinutes(1), settings.LoginPerLocalIdPerMinute);
    }

    public async Task<LoginResult> LoginAsync(string localId, ReadOnlyMemory<byte> secret, string clientIp, CancellationToken cancellationToken)
    {
        if (!GuestCredentials.IsValidLocalId(localId) || !GuestCredentials.IsValidSecret(secret.Span))
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid guest credential format.");
        }

        if (_perLocalId.IsOver(localId))
        {
            throw LoginRateLimits.RateLimited(_perLocalId.RetryAfterSeconds(localId));
        }

        _limits.Enter(clientIp);
        _perLocalId.Increment(localId);

        var mappingKey = GuestCredentials.MappingKey(localId);
        var playerId = await ResolvePlayerAsync(mappingKey, secret, clientIp, cancellationToken);
        return await _completion.CompleteAsync(playerId, mappingKey, cancellationToken);
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
            _limits.Failed(clientIp);
            _metrics.Login("bad_credentials");
            throw new GameServerException(CoreErrorCodes.Unauthenticated, "Guest secret mismatch.");
        }

        return existing.Content["playerId"]!.GetValue<string>();
    }
}
