using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Limits;
using GameServer.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>Provider settings (section <c>Identity:Providers</c>). Secrets come from the secret provider (OPS-08).</summary>
public sealed class IdentityProviderOptions
{
    public const string SectionName = "Identity:Providers";

    /// <summary>
    /// Fake Play Games / Game Center adapters (Development/CI, <see cref="FakeIdentityAdapter"/>). Allowed only in
    /// Development/Test; defaults to true in Development only (a Test deployment uses the real providers unless set).
    /// </summary>
    public bool? UseFake { get; set; }

    /// <summary>
    /// Deadline of one verification, all provider calls included (PERF-03). Capped at 80 % of
    /// <c>GameServer:Limits:PlayerTimeoutSeconds</c> so the provider error, not a generic timeout, reaches the client.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Verifications in flight per instance (bulkhead): a slow provider cannot hold every request slot.</summary>
    public int MaxConcurrentVerifications { get; set; } = 32;

    /// <summary>Link attempts per player per minute (each one may call the provider).</summary>
    public int LinkPerPlayerPerMinute { get; set; } = 10;

    public PlayGamesOptions PlayGames { get; set; } = new();

    public GameCenterOptions GameCenter { get; set; } = new();
}

public sealed class PlayGamesOptions
{
    public bool Enabled { get; set; }

    /// <summary>OAuth web client id of the game's Play Games project.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth client secret; secret provider only (environment variable <c>Identity__Providers__PlayGames__ClientSecret</c>).</summary>
    public string ClientSecret { get; set; } = string.Empty;

    public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";

    public string PlayersMeUri { get; set; } = "https://games.googleapis.com/games/v1/players/me";
}

public sealed class GameCenterOptions
{
    public bool Enabled { get; set; }

    /// <summary>Hosts the public key may be downloaded from (https, path <c>/public-key/</c>, no query).</summary>
    public List<string> PublicKeyHosts { get; set; } = ["static.gc.apple.com"];

    /// <summary>Bundle ids of the game's iOS/macOS apps; the signed bundle id must be one of them.</summary>
    public List<string> BundleIds { get; set; } = [];

    /// <summary>Allowed distance between server time and the signature timestamp (TBD-03: 10 minutes).</summary>
    public int MaxTimestampSkewMinutes { get; set; } = 10;

    /// <summary>How long a downloaded public key certificate is reused (TBD-03: 24 h).</summary>
    public int CertificateCacheHours { get; set; } = 24;

    /// <summary>Trust anchors (DER, base64). Empty = <see cref="GameCenterTrust.DigiCertTrustedRootG4"/>.</summary>
    public List<string> TrustedRootsBase64 { get; set; } = [];

    /// <summary>Intermediate CAs (DER, base64). Empty = <see cref="GameCenterTrust.DigiCertTrustedG4CodeSigningCa1"/>.</summary>
    public List<string> IntermediatesBase64 { get; set; } = [];

    /// <summary>Organization (O=) the signing certificate must name.</summary>
    public string RequiredOrganization { get; set; } = "Apple Inc.";
}

/// <summary>Fails startup on unsafe or incomplete provider settings (PERF-08, OPS-08).</summary>
internal sealed class IdentityProviderOptionsValidator(GameServerOptions gameServer) : IValidateOptions<IdentityProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityProviderOptions options)
    {
        var errors = new List<string>();
        var useFake = IdentityProviders.UsesFake(options, gameServer);
        if (useFake && !gameServer.AllowsDevelopmentTools)
        {
            errors.Add($"[module identity] Identity:Providers:UseFake is true in '{gameServer.DeploymentEnvironment}'. Fix: configure Play Games / Game Center (IDN-03).");
        }

        if (options.TimeoutSeconds is < 1 or > 30)
        {
            errors.Add("[module identity] Identity:Providers:TimeoutSeconds must be 1..30 (PERF-03).");
        }

        if (options.MaxConcurrentVerifications is < 1 or > 1000 || options.LinkPerPlayerPerMinute is < 1 or > 1000)
        {
            errors.Add("[module identity] Identity:Providers:MaxConcurrentVerifications and LinkPerPlayerPerMinute must be 1..1000.");
        }

        if (!useFake && options.PlayGames.Enabled && (string.IsNullOrEmpty(options.PlayGames.ClientId) || string.IsNullOrEmpty(options.PlayGames.ClientSecret)))
        {
            errors.Add("[module identity] Identity:Providers:PlayGames is enabled without ClientId/ClientSecret. Fix: provide them through the secret provider (OPS-08).");
        }

        var gameCenter = options.GameCenter;
        if (!useFake && gameCenter.Enabled && gameCenter.BundleIds.Count == 0)
        {
            errors.Add("[module identity] Identity:Providers:GameCenter is enabled without BundleIds (IDN-03).");
        }

        if (gameCenter.MaxTimestampSkewMinutes is < 1 or > 10)
        {
            errors.Add("[module identity] Identity:Providers:GameCenter:MaxTimestampSkewMinutes must be 1..10 (TBD-03).");
        }

        if (gameCenter.CertificateCacheHours is < 0 or > 24)
        {
            errors.Add("[module identity] Identity:Providers:GameCenter:CertificateCacheHours must be 0..24 (TBD-03).");
        }

        if (gameCenter.PublicKeyHosts.Count == 0 || gameCenter.PublicKeyHosts.Any(h => !(h.Equals("apple.com", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add("[module identity] Identity:Providers:GameCenter:PublicKeyHosts must list apple.com hosts (IDN-03).");
        }

        foreach (var (setting, values) in new[] { ("TrustedRootsBase64", gameCenter.TrustedRootsBase64), ("IntermediatesBase64", gameCenter.IntermediatesBase64) })
        {
            foreach (var value in values)
            {
                try
                {
                    X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value)).Dispose();
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    errors.Add($"[module identity] Identity:Providers:GameCenter:{setting} has an entry that is not a base64 DER certificate. Fix: export the certificate as DER and base64-encode it.");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>
/// Adapter per provider name (IDN-03, EXT-01): the built-in Play Games and Game Center adapters when enabled (fakes in
/// Development by default), plus every <see cref="IIdentityProviderAdapter"/> the game registers. Two adapters for one
/// provider fail startup. Every verification runs under one deadline (<see cref="IdentityProviderOptions.TimeoutSeconds"/>)
/// and a per-instance bulkhead (<see cref="IdentityProviderOptions.MaxConcurrentVerifications"/>), so a slow provider
/// answers PROVIDER_UNAVAILABLE instead of holding request slots.
/// </summary>
public sealed class IdentityProviders : IDisposable
{
    public const string HttpClientName = "identity-providers";

    private readonly Dictionary<string, IIdentityProviderAdapter> _adapters = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _owned = [];
    private readonly SemaphoreSlim _bulkhead;
    private readonly TimeSpan _deadline;
    private readonly FixedWindowCounter _linksPerPlayer;

    public IdentityProviders(
        IServiceProvider services,
        IOptions<IdentityProviderOptions> options,
        GameServerOptions gameServer,
        IClock clock,
        IEnumerable<IIdentityProviderAdapter> gameAdapters)
    {
        var settings = options.Value;
        _bulkhead = new SemaphoreSlim(settings.MaxConcurrentVerifications, settings.MaxConcurrentVerifications);
        _deadline = TimeSpan.FromSeconds(Math.Min(settings.TimeoutSeconds, gameServer.Limits.PlayerTimeoutSeconds * 0.8));
        _linksPerPlayer = new FixedWindowCounter(clock, TimeSpan.FromMinutes(1), settings.LinkPerPlayerPerMinute);
        var builtIn = new List<IIdentityProviderAdapter>();
        if (UsesFake(settings, gameServer))
        {
            builtIn.Add(new FakeIdentityAdapter(PlayGamesCredential.ProviderName));
            builtIn.Add(new FakeIdentityAdapter(GameCenterCredential.ProviderName));
        }
        else
        {
            if (settings.PlayGames.Enabled)
            {
                builtIn.Add(ActivatorUtilities.CreateInstance<PlayGamesAdapter>(services));
            }

            if (settings.GameCenter.Enabled)
            {
                builtIn.Add(ActivatorUtilities.CreateInstance<GameCenterAdapter>(services));
            }
        }

        _owned.AddRange(builtIn.OfType<IDisposable>());
        foreach (var adapter in builtIn.Concat(gameAdapters))
        {
            if (!ProviderIdentities.IsValidProvider(adapter.Provider))
            {
                throw new GameServerStartupException(
                    $"[module identity] Identity provider adapter {adapter.GetType().Name} has invalid name '{adapter.Provider}'. Fix: 2-32 of [a-z0-9], starting with a letter, not 'guest'.");
            }

            if (!_adapters.TryAdd(adapter.Provider, adapter))
            {
                throw new GameServerStartupException(
                    $"[module identity] Two identity provider adapters for '{adapter.Provider}' ({_adapters[adapter.Provider].GetType().Name}, {adapter.GetType().Name}). Fix: register one adapter per provider (EXT-01).");
            }
        }
    }

    public IReadOnlyCollection<string> Names => _adapters.Keys;

    /// <summary>Fakes by default in Development only; Test and beyond use the configured providers unless told otherwise.</summary>
    public static bool UsesFake(IdentityProviderOptions options, GameServerOptions gameServer) => options.UseFake ?? gameServer.IsDevelopment;

    /// <summary>Counts a link attempt of <paramref name="playerId"/>; RATE_LIMITED above the per-minute budget.</summary>
    public void EnterLink(string playerId)
    {
        if (_linksPerPlayer.Increment(playerId) > _linksPerPlayer.Limit)
        {
            throw LoginRateLimits.RateLimited(_linksPerPlayer.RetryAfterSeconds(playerId));
        }
    }

    /// <summary>Verifies with the credential's provider; 400 when no adapter serves it.</summary>
    public async ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential credential, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(credential.Provider, out var adapter))
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"Identity provider '{credential.Provider}' is not enabled.");
        }

        if (!await _bulkhead.WaitAsync(0, cancellationToken))
        {
            throw new GameServerException(IdentityErrors.ProviderUnavailable, "Too many provider verifications in flight.") { RetryAfterMs = 1000 };
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_deadline);
            VerifiedIdentity identity;
            try
            {
                identity = await adapter.VerifyAsync(credential, deadline.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GameServerException(IdentityErrors.ProviderUnavailable, $"Identity provider '{adapter.Provider}' did not answer in time.", ex) { RetryAfterMs = 2000 };
            }

            if (identity.Provider != adapter.Provider)
            {
                throw new InvalidOperationException($"Adapter {adapter.GetType().Name} returned provider '{identity.Provider}' instead of '{adapter.Provider}'.");
            }

            _ = identity.MappingKey; // validates the subject
            return identity;
        }
        finally
        {
            _bulkhead.Release();
        }
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _bulkhead.Dispose();
    }
}

/// <summary>
/// Development/CI stand-in for Play Games and Game Center (never in Production, see validator). Play Games codes
/// <c>fake:{playerId}</c> map to <c>pgs:{playerId}</c>; Game Center credentials with public key URL <c>fake:</c> map to
/// <c>gamecenter:{teamPlayerId}</c>. Code <c>fake-unavailable</c> simulates a provider outage.
/// </summary>
public sealed class FakeIdentityAdapter(string provider) : IIdentityProviderAdapter
{
    public const string Prefix = "fake:";
    public const string UnavailableCode = "fake-unavailable";

    public string Provider { get; } = provider;

    public ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential credential, CancellationToken cancellationToken) =>
        credential switch
        {
            PlayGamesCredential { ServerAuthCode: UnavailableCode } =>
                throw new GameServerException(IdentityErrors.ProviderUnavailable, "Fake provider outage.") { RetryAfterMs = 2000 },
            PlayGamesCredential pgs when pgs.ServerAuthCode.StartsWith(Prefix, StringComparison.Ordinal) && ProviderIdentities.IsValidSubject(pgs.ServerAuthCode[Prefix.Length..]) =>
                ValueTask.FromResult(new VerifiedIdentity(Provider, pgs.ServerAuthCode[Prefix.Length..], new Dictionary<string, string>())),
            GameCenterCredential gc when gc.PublicKeyUrl == Prefix && ProviderIdentities.IsValidSubject(gc.TeamPlayerId) =>
                ValueTask.FromResult(new VerifiedIdentity(Provider, gc.TeamPlayerId, new Dictionary<string, string> { [GameCenterAdapter.UnverifiedGamePlayerIdAttribute] = gc.GamePlayerId })),
            _ => throw new GameServerException(IdentityErrors.IdentityProofInvalid, $"Fake {Provider} credential rejected."),
        };
}
