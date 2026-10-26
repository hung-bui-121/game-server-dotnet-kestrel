using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>A validated GM cookie session.</summary>
public sealed record GmSession(string Key, string StaffId, DateTimeOffset ExpiresAt);

/// <summary>
/// GM sessions (research R5): opaque 256-bit id in cookie <c>gs_gm</c>, stored by SHA-256 hash in <c>gm_sessions</c>
/// with an 8 h expiry, scoped to this game and environment. The id is never logged or stored in clear.
/// </summary>
public sealed class GmSessionService
{
    public const string Collection = "gm_sessions";

    private readonly IKeyValueStore _store;
    private readonly IClock _clock;
    private readonly GameServerOptions _gameServer;
    private readonly GmOptions _options;
    private readonly ExpiringCache<string, GmSession> _cache;

    public GmSessionService(IKeyValueStore store, IClock clock, GameServerOptions gameServer, IOptions<GmOptions> options)
    {
        _store = store;
        _clock = clock;
        _gameServer = gameServer;
        _options = options.Value;
        _cache = new ExpiringCache<string, GmSession>(clock, TimeSpan.FromSeconds(_options.CacheSeconds));
    }

    public static string KeyOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(string staffId, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _clock.UtcNow;
        var lifetime = TimeSpan.FromHours(_options.SessionHours);
        var document = new JsonObject
        {
            ["staffId"] = staffId,
            ["gameId"] = _gameServer.GameId,
            ["environmentId"] = _gameServer.EnvironmentId,
            ["createdAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
            ["expiresAtUtc"] = AggregateDocumentSerializer.FormatTime(now + lifetime),
        };
        if (!await _store.InsertAsync(new DocumentKey(Collection, KeyOf(token)), document, lifetime, cancellationToken))
        {
            throw new InvalidOperationException("GM session key collision.");
        }

        return (token, now + lifetime);
    }

    /// <summary>Returns the session if it exists, has not expired and belongs to this game and environment.</summary>
    public async Task<GmSession?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var key = KeyOf(token);
        if (!_cache.TryGet(key, out var session))
        {
            var document = await _store.GetAsync(new DocumentKey(Collection, key), cancellationToken);
            var content = document?.Content;
            if (content is null
                || content["gameId"]?.GetValue<string>() != _gameServer.GameId
                || content["environmentId"]?.GetValue<string>() != _gameServer.EnvironmentId)
            {
                // Unknown tokens are not cached: random cookies must not grow the cache.
                return null;
            }

            session = new GmSession(key, content["staffId"]!.GetValue<string>(), AggregateDocumentSerializer.ParseTime(content["expiresAtUtc"]!.GetValue<string>()));
            _cache.Set(key, session);
        }

        return session.ExpiresAt > _clock.UtcNow ? session : null;
    }

    public async Task RemoveAsync(string sessionKey, CancellationToken cancellationToken)
    {
        _cache.Remove(sessionKey);
        await _store.RemoveAsync(new DocumentKey(Collection, sessionKey), cancellationToken);
    }
}
