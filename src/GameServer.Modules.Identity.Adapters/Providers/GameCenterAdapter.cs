using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GameServer.Errors;
using GameServer.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Apple Game Center (IDN-03, V19 / research R3): verifies the <c>fetchItems(forIdentityVerificationSignature:)</c>
/// signature. The public key URL must be https on an allowlisted Apple host (default <c>static.gc.apple.com</c>) under
/// <c>/public-key/</c> without query; redirects are not followed. The certificate must chain to a pinned root, name
/// <c>O=Apple Inc.</c>, allow code signing and be valid now; it is cached per URL for at most 24 h. The signature is
/// RSASSA-PKCS1-v1_5 SHA-256 over <c>teamPlayerID ‖ bundleID ‖ timestamp (UInt64 big-endian) ‖ salt</c>; the timestamp
/// must be within 10 minutes of server time and the bundle id one of the game's. The subject is <c>teamPlayerID</c>
/// (stable across reinstalls and the team's games). <c>gamePlayerID</c> is not covered by the signature: it is stored
/// as <see cref="UnverifiedGamePlayerIdAttribute"/> and must not be used to grant anything.
/// Failures the client caused (wrong signature, stale timestamp, unknown key path, expired certificate) are
/// IDENTITY_PROOF_INVALID; failures of Apple's own key (chain or subject no longer matching the pins, 5xx, redirect)
/// are logged as errors and answered PROVIDER_UNAVAILABLE, so a pin rotation does not lock players out as "bad proof".
/// </summary>
public sealed class GameCenterAdapter : IIdentityProviderAdapter, IDisposable
{
    public const int MaxCertificateBytes = 16 * 1024;

    /// <summary>Attribute holding the client-asserted <c>gamePlayerID</c> (not signed by Apple).</summary>
    public const string UnverifiedGamePlayerIdAttribute = "unverifiedGamePlayerId";

    private const int MaxCachedCertificates = 16;
    private static readonly Oid CodeSigning = new("1.3.6.1.5.5.7.3.3");
    private static readonly Oid Organization = new("2.5.4.10");

    private readonly IHttpClientFactory _httpClients;
    private readonly IClock _clock;
    private readonly ILogger<GameCenterAdapter> _logger;
    private readonly GameCenterOptions _settings;
    private readonly X509Certificate2Collection _roots;
    private readonly X509Certificate2Collection _intermediates;
    private readonly ConcurrentDictionary<string, CachedCertificate> _certificates = new(StringComparer.Ordinal);

    public GameCenterAdapter(IHttpClientFactory httpClients, IClock clock, IOptions<IdentityProviderOptions> options, ILogger<GameCenterAdapter> logger)
    {
        _httpClients = httpClients;
        _clock = clock;
        _logger = logger;
        _settings = options.Value.GameCenter;
        _roots = Load(_settings.TrustedRootsBase64, GameCenterTrust.DigiCertTrustedRootG4);
        _intermediates = Load(_settings.IntermediatesBase64, GameCenterTrust.DigiCertTrustedG4CodeSigningCa1);
    }

    public string Provider => GameCenterCredential.ProviderName;

    private sealed record CachedCertificate(X509Certificate2 Certificate, DateTimeOffset ExpiresAt);

    public async ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential credential, CancellationToken cancellationToken)
    {
        if (credential is not GameCenterCredential gc
            || !ProviderIdentities.IsValidSubject(gc.TeamPlayerId)
            || gc.GamePlayerId.Length > ProviderIdentities.MaxSubjectLength
            || gc.Signature.Length is 0 or > 1024
            || gc.Salt.Length is 0 or > 256
            || gc.BundleId.Length is 0 or > 256)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "game_center needs team_player_id, signature (≤ 1 KiB), salt (≤ 256 B), timestamp and bundle_id.");
        }

        if (!_settings.BundleIds.Contains(gc.BundleId, StringComparer.Ordinal))
        {
            throw Invalid("bundle id is not one of this game's");
        }

        var now = _clock.UtcNow;
        var signedAt = gc.Timestamp > (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds((long)gc.Timestamp);
        if ((now - signedAt).Duration() > TimeSpan.FromMinutes(_settings.MaxTimestampSkewMinutes))
        {
            throw Invalid("timestamp is too far from server time");
        }

        var url = ValidateUrl(gc.PublicKeyUrl, _settings.PublicKeyHosts);
        var certificate = await GetCertificateAsync(url, now, cancellationToken);
        using var rsa = certificate.GetRSAPublicKey() ?? throw Invalid("certificate has no RSA key");
        if (!rsa.VerifyData(SignedData(gc), gc.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            throw Invalid("signature does not match");
        }

        return new VerifiedIdentity(
            Provider,
            gc.TeamPlayerId,
            new Dictionary<string, string>(StringComparer.Ordinal) { [UnverifiedGamePlayerIdAttribute] = gc.GamePlayerId, ["bundleId"] = gc.BundleId });
    }

    /// <summary><c>teamPlayerID ‖ bundleID</c> (UTF-8) <c>‖ timestamp</c> (UInt64 big-endian) <c>‖ salt</c>.</summary>
    public static byte[] SignedData(GameCenterCredential credential)
    {
        var team = Encoding.UTF8.GetBytes(credential.TeamPlayerId);
        var bundle = Encoding.UTF8.GetBytes(credential.BundleId);
        var data = new byte[team.Length + bundle.Length + 8 + credential.Salt.Length];
        team.CopyTo(data, 0);
        bundle.CopyTo(data, team.Length);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(team.Length + bundle.Length, 8), credential.Timestamp);
        credential.Salt.CopyTo(data, team.Length + bundle.Length + 8);
        return data;
    }

    /// <summary>https, an allowlisted host, default port, no user info, path under <c>/public-key/</c>, no query or fragment.</summary>
    public static Uri ValidateUrl(string publicKeyUrl, IReadOnlyCollection<string> hosts)
    {
        if (!Uri.TryCreate(publicKeyUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !url.IsDefaultPort
            || url.UserInfo.Length > 0
            || url.HostNameType != UriHostNameType.Dns
            || !hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase)
            || !url.AbsolutePath.StartsWith("/public-key/", StringComparison.Ordinal)
            || url.AbsolutePath.Contains("..", StringComparison.Ordinal)
            || url.Query.Length > 0
            || url.Fragment.Length > 0)
        {
            throw Invalid("public key URL is not an allowed Apple key URL");
        }

        return url;
    }

    public void Dispose()
    {
        foreach (var cached in _certificates.Values)
        {
            cached.Certificate.Dispose();
        }

        foreach (var certificate in _roots.Concat(_intermediates))
        {
            certificate.Dispose();
        }
    }

    private async Task<X509Certificate2> GetCertificateAsync(Uri url, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = url.AbsoluteUri;
        if (_certificates.TryGetValue(key, out var cached) && now < cached.ExpiresAt && now >= cached.ExpiresAt - TimeSpan.FromHours(_settings.CertificateCacheHours))
        {
            return cached.Certificate;
        }

        var certificate = await DownloadAsync(url, cancellationToken);
        if (!IsTrusted(certificate, now, out var clientFault, out var reason))
        {
            certificate.Dispose();
            if (clientFault)
            {
                throw Invalid(reason);
            }

            // Apple's current key no longer matches the pins: an operator problem, not the player's.
            _logger.LogError("Game Center public key {Url} rejected: {Reason}. Update Identity:Providers:GameCenter pins.", key, reason);
            throw Unavailable();
        }

        // Never past the certificate's own expiry. Old entries are left to the GC (they may still be in use); the URL
        // allowlist keeps the key space to Apple's few key files.
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var expiresAt = now + TimeSpan.FromHours(_settings.CertificateCacheHours);
        if (_certificates.Count < MaxCachedCertificates || _certificates.ContainsKey(key))
        {
            _certificates[key] = new CachedCertificate(certificate, expiresAt < notAfter ? expiresAt : notAfter);
        }

        return certificate;
    }

    private async Task<X509Certificate2> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClients.CreateClient(IdentityProviders.HttpClientName);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            {
                throw Invalid("public key URL names no key");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Redirects are not followed (no hop off the allowlisted host).
                _logger.LogWarning("Game Center public key download answered {Status}.", (int)response.StatusCode);
                throw Unavailable();
            }

            if (response.Content.Headers.ContentLength > MaxCertificateBytes)
            {
                throw Invalid("public key certificate is too large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxCertificateBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            if (total > MaxCertificateBytes)
            {
                throw Invalid("public key certificate is too large");
            }

            try
            {
                return X509CertificateLoader.LoadCertificate(buffer.AsSpan(0, total));
            }
            catch (CryptographicException)
            {
                throw Invalid("public key URL did not return a certificate");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Game Center public key download failed.");
            throw Unavailable();
        }
    }

    private bool IsTrusted(X509Certificate2 certificate, DateTimeOffset now, out bool clientFault, out string reason)
    {
        clientFault = false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.ExtraStore.AddRange(_intermediates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(CodeSigning);
        if (!chain.Build(certificate))
        {
            var statuses = chain.ChainStatus.Select(s => s.Status).Distinct().ToArray();

            // Only an expired (or not yet valid) key is the client's doing: an old key URL. Anything else means the pins
            // no longer describe Apple's chain.
            clientFault = statuses.All(s => s == X509ChainStatusFlags.NotTimeValid);
            reason = "certificate does not chain to a trusted root (" + string.Join(", ", statuses) + ")";
            return false;
        }

        var organization = certificate.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(rdn => rdn.GetSingleElementType().Value == Organization.Value)
            .Select(rdn => rdn.GetSingleElementValue())
            .FirstOrDefault();
        if (!string.Equals(organization, _settings.RequiredOrganization, StringComparison.Ordinal))
        {
            reason = "certificate is not issued to " + _settings.RequiredOrganization;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static X509Certificate2Collection Load(List<string> configured, string fallback)
    {
        var collection = new X509Certificate2Collection();
        foreach (var base64 in configured.Count > 0 ? configured : [fallback])
        {
            collection.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64)));
        }

        return collection;
    }

    private static GameServerException Invalid(string reason) =>
        new(IdentityErrors.IdentityProofInvalid, "Game Center proof rejected: " + reason + ".");

    private static GameServerException Unavailable() =>
        new(IdentityErrors.ProviderUnavailable, "Game Center public key is unavailable.") { RetryAfterMs = 2000 };
}
