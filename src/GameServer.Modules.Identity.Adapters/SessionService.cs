using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>A validated player session (data-model.md, <c>sessions</c>).</summary>
public sealed record SessionRecord(string Key, string PlayerId, long Epoch, DateTimeOffset CreatedAt, DateTimeOffset LastTouchAt, StoredDocument Document);

/// <summary>
/// Opaque 256-bit session tokens stored by hash (IDN-08): sliding 30-day and absolute 90-day expiry, lazy touch,
/// per-instance cache ≤ 60 s. Tokens are never logged or stored in clear.
/// </summary>
public sealed class SessionService
{
    public const string Collection = "sessions";

    private readonly IKeyValueStore _store;
    private readonly IClock _clock;
    private readonly GameServerOptions _gameServer;
    private readonly IdentityOptions _options;
    private readonly ExpiringCache<string, SessionRecord> _cache;

    public SessionService(IKeyValueStore store, IClock clock, GameServerOptions gameServer, IOptions<IdentityOptions> options)
    {
        _store = store;
        _clock = clock;
        _gameServer = gameServer;
        _options = options.Value;
        _cache = new ExpiringCache<string, SessionRecord>(clock, TimeSpan.FromSeconds(_options.CacheSeconds));
    }

    /// <summary>Hex SHA-256 of the token: the document key.</summary>
    public static string KeyOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(string playerId, long epoch, CancellationToken cancellationToken)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _clock.UtcNow;
        var expiresAt = ExpiresAt(now, now);
        var document = new JsonObject
        {
            ["playerId"] = playerId,
            ["epoch"] = epoch,
            ["gameId"] = _gameServer.GameId,
            ["environmentId"] = _gameServer.EnvironmentId,
            ["createdAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
            ["lastTouchAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
        };
        if (!await _store.InsertAsync(new DocumentKey(Collection, KeyOf(token)), document, expiresAt - now, cancellationToken))
        {
            throw new InvalidOperationException("Session key collision.");
        }

        return (token, expiresAt);
    }

    /// <summary>Returns the session when it exists and has not expired (sliding or absolute); otherwise null.</summary>
    public async Task<SessionRecord?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var key = KeyOf(token);
        if (!_cache.TryGet(key, out var record))
        {
            var document = await _store.GetAsync(new DocumentKey(Collection, key), cancellationToken);
            if (document is null)
            {
                return null;
            }

            record = new SessionRecord(
                key,
                document.Content["playerId"]!.GetValue<string>(),
                JsonNumbers.ToInt64(document.Content["epoch"]) ?? 0,
                AggregateDocumentSerializer.ParseTime(document.Content["createdAtUtc"]!.GetValue<string>()),
                AggregateDocumentSerializer.ParseTime(document.Content["lastTouchAtUtc"]!.GetValue<string>()),
                document);
            _cache.Set(key, record);
        }

        return ExpiresAt(record.CreatedAt, record.LastTouchAt) > _clock.UtcNow ? record : null;
    }

    /// <summary>Extends the sliding expiry when the last touch is older than the touch interval (best effort, CAS).</summary>
    public async Task TouchIfDueAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (now - record.LastTouchAt < TimeSpan.FromHours(_options.TouchAfterHours))
        {
            return;
        }

        var content = (JsonObject)record.Document.Content.DeepClone();
        content["lastTouchAtUtc"] = AggregateDocumentSerializer.FormatTime(now);
        var expiresAt = ExpiresAt(record.CreatedAt, now);
        if (await _store.ReplaceAsync(record.Document, content, expiresAt - now, cancellationToken))
        {
            _cache.Remove(record.Key);
        }
    }

    public async Task RemoveAsync(string sessionKey, CancellationToken cancellationToken)
    {
        _cache.Remove(sessionKey);
        await _store.RemoveAsync(new DocumentKey(Collection, sessionKey), cancellationToken);
    }

    private DateTimeOffset ExpiresAt(DateTimeOffset createdAt, DateTimeOffset lastTouchAt)
    {
        var sliding = lastTouchAt + TimeSpan.FromDays(_options.SessionSlidingDays);
        var absolute = createdAt + TimeSpan.FromDays(_options.SessionAbsoluteDays);
        return sliding < absolute ? sliding : absolute;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
