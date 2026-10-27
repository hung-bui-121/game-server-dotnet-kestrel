using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>A validated GM cookie session (ticket held server-side).</summary>
public sealed record GmSession(string Key, string StaffId, long Epoch, DateTimeOffset ExpiresAt, DateTimeOffset LastActiveAt);

/// <summary>
/// GM sessions (SEC-04, SEC-10, research R4): a random 256-bit session id, protected (encrypted + authenticated) with
/// the deployment's shared Data Protection key ring, is the only cookie content. The ticket lives in
/// <c>gm_sessions/{sha256(id)}</c> with the staff, the staff session epoch, an absolute expiry and an idle timeout.
/// The id is never logged or stored in clear.
/// </summary>
public sealed class GmSessionService
{
    public const string Collection = "gm_sessions";
    public const string ProtectorPurpose = "GameServer.Gm.Session.v1";

    /// <summary>Last activity is written at most this often (lazy touch; CAS conflicts are ignored).</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly IKeyValueStore _store;
    private readonly IClock _clock;
    private readonly GameServerOptions _gameServer;
    private readonly GmOptions _options;
    private readonly IDataProtector _protector;
    private readonly ExpiringCache<string, GmSession> _cache;

    public GmSessionService(IKeyValueStore store, IClock clock, GameServerOptions gameServer, IOptions<GmOptions> options, IDataProtectionProvider dataProtection)
    {
        _store = store;
        _clock = clock;
        _gameServer = gameServer;
        _options = options.Value;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _cache = new ExpiringCache<string, GmSession>(clock, TimeSpan.FromSeconds(_options.CacheSeconds));
    }

    public static string KeyOf(string sessionId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    /// <summary>Creates the ticket and returns the protected cookie value.</summary>
    public async Task<(string CookieValue, DateTimeOffset ExpiresAt)> CreateAsync(StaffRecord staff, CancellationToken cancellationToken)
    {
        var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _clock.UtcNow;
        var lifetime = TimeSpan.FromHours(_options.SessionHours);
        var document = new JsonObject
        {
            ["staffId"] = staff.StaffId,
            ["gameId"] = _gameServer.GameId,
            ["environmentId"] = _gameServer.EnvironmentId,
            ["epoch"] = staff.SessionEpoch,
            ["createdAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
            ["lastActiveAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
            ["expiresAtUtc"] = AggregateDocumentSerializer.FormatTime(now + lifetime),
        };
        if (!await _store.InsertAsync(new DocumentKey(Collection, KeyOf(sessionId)), document, lifetime, cancellationToken))
        {
            throw new InvalidOperationException("GM session key collision.");
        }

        return (_protector.Protect(sessionId), now + lifetime);
    }

    /// <summary>
    /// Returns the ticket when the cookie decrypts with the shared key ring, the ticket exists for this game and
    /// environment, and neither the absolute expiry nor the idle timeout has passed. The staff epoch is checked by
    /// the authentication handler.
    /// </summary>
    public async Task<GmSession?> ValidateAsync(string cookieValue, CancellationToken cancellationToken)
    {
        string sessionId;
        try
        {
            sessionId = _protector.Unprotect(cookieValue);
        }
        catch (CryptographicException)
        {
            // Forged, tampered, or protected with a key this deployment never had.
            return null;
        }

        var key = KeyOf(sessionId);
        var now = _clock.UtcNow;
        if (!_cache.TryGet(key, out var session) || now - session.LastActiveAt >= TouchInterval)
        {
            var document = await _store.GetAsync(new DocumentKey(Collection, key), cancellationToken);
            session = Parse(key, document);
            if (session is null)
            {
                // Unknown tokens are not cached: random cookies must not grow the cache.
                _cache.Remove(key);
                return null;
            }

            if (now - session.LastActiveAt >= TouchInterval && now - session.LastActiveAt < Idle)
            {
                session = await TouchAsync(document!, session, now, cancellationToken);
            }

            _cache.Set(key, session);
        }

        return session.ExpiresAt > now && now - session.LastActiveAt < Idle ? session : null;
    }

    public async Task RemoveAsync(string sessionKey, CancellationToken cancellationToken)
    {
        _cache.Remove(sessionKey);
        await _store.RemoveAsync(new DocumentKey(Collection, sessionKey), cancellationToken);
    }

    private TimeSpan Idle => TimeSpan.FromMinutes(_options.IdleMinutes);

    private GmSession? Parse(string key, StoredDocument? document)
    {
        var content = document?.Content;
        if (content is null
            || content["gameId"]?.GetValue<string>() != _gameServer.GameId
            || content["environmentId"]?.GetValue<string>() != _gameServer.EnvironmentId)
        {
            return null;
        }

        var created = content["createdAtUtc"]!.GetValue<string>();
        return new GmSession(
            key,
            content["staffId"]!.GetValue<string>(),
            content["epoch"]?.GetValue<long>() ?? 0,
            AggregateDocumentSerializer.ParseTime(content["expiresAtUtc"]!.GetValue<string>()),
            AggregateDocumentSerializer.ParseTime(content["lastActiveAtUtc"]?.GetValue<string>() ?? created));
    }

    private async Task<GmSession> TouchAsync(StoredDocument document, GmSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var content = (JsonObject)document.Content.DeepClone();
        content["lastActiveAtUtc"] = AggregateDocumentSerializer.FormatTime(now);
        var remaining = session.ExpiresAt - now;
        if (remaining > TimeSpan.Zero)
        {
            // A lost race only means another request touched it first.
            await _store.ReplaceAsync(document, content, remaining, cancellationToken);
        }

        return session with { LastActiveAt = now };
    }
}
