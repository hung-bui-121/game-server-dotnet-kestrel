using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Modules.Identity;

/// <summary>
/// Login with a provider identity (IDN-03, IDN-11): verify → mapping → account checks of the mapped player → (switch
/// check) → common login end. A deleted or suspended mapped account answers its own code before SWITCH_ACCOUNT: switching
/// into an account that can only be refused is never offered (PRIV-01, D5). The mapped
/// player is returned whatever the device's guest credential (reinstall). An unmapped identity creates a player: the
/// mapping is inserted through <see cref="ClaimStore"/> (a transaction, like the claim of <c>identity.link</c>, so the two
/// cannot race through mixed KV/transaction writes); a lost race returns the winner's player. When the device presents a valid
/// session of another player and did not confirm, the answer is 409 SWITCH_ACCOUNT: accounts are never merged.
/// </summary>
public sealed class ProviderLoginService(
    IdentityProviders providers,
    IKeyValueStore store,
    ClaimStore claims,
    SessionService sessions,
    ProfileCache profiles,
    LoginCompletion completion,
    LoginRateLimits limits,
    IClock clock,
    GameServerOptions gameServer,
    IdentityMetrics metrics,
    ILogger<ProviderLoginService> logger)
{
    public async Task<LoginResult> LoginAsync(
        IdentityCredential credential,
        bool switchAccount,
        string? currentToken,
        string clientIp,
        CancellationToken cancellationToken)
    {
        limits.Enter(clientIp);

        VerifiedIdentity identity;
        try
        {
            identity = await providers.VerifyAsync(credential, cancellationToken);
        }
        catch (GameServerException ex) when (ex.Code == IdentityErrors.IdentityProofInvalid)
        {
            limits.Failed(clientIp);
            metrics.Login("bad_credentials");
            throw;
        }

        var key = new DocumentKey(IdentityCollections.Identities, identity.MappingKey);
        var mapped = PlayerOf(await store.GetAsync(key, cancellationToken));
        if (mapped is not null)
        {
            await EnsureAccessibleAsync(mapped, cancellationToken);
        }

        if (!switchAccount && await CurrentPlayerAsync(currentToken, cancellationToken) is { } current && current != mapped)
        {
            metrics.Login(CoreErrorCodes.SwitchAccount.Name);
            throw new GameServerException(CoreErrorCodes.SwitchAccount, $"The {identity.Provider} identity is not the current player's.")
            {
                Details = new Dictionary<string, string>
                {
                    ["provider"] = identity.Provider,
                    ["account"] = mapped is null ? "new" : "existing",
                },
            };
        }

        var playerId = mapped ?? await CreateMappingAsync(key, identity, cancellationToken);
        return await completion.CompleteAsync(playerId, identity.MappingKey, cancellationToken);
    }

    private static string? PlayerOf(StoredDocument? mapping) => mapping?.Content["playerId"]?.GetValue<string>();

    /// <summary>
    /// Account checks of the mapped player (fresh read). A missing profile is not refused here: a first login whose
    /// earlier attempt crashed after the mapping insert continues; <see cref="LoginCompletion"/> decides with the anchor.
    /// </summary>
    private async Task EnsureAccessibleAsync(string playerId, CancellationToken cancellationToken)
    {
        if (await profiles.ReadFreshAsync(playerId, cancellationToken) is { } profile && AccountChecks.CheckAccess(profile, clock.UtcNow) is { } denial)
        {
            metrics.Login(denial.Code.Name);
            throw new GameServerException(denial.Code, $"Login refused for {playerId}.") { Details = denial.Details };
        }
    }

    /// <summary>Player of a valid, current session sent with the login; anything else counts as no session.</summary>
    private async Task<string?> CurrentPlayerAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128 || await sessions.ValidateAsync(token, cancellationToken) is not { } session)
        {
            return null;
        }

        // No profile document: the session's account is purged, so there is no current player (research R9).
        var profile = await profiles.GetAsync(session.PlayerId, cancellationToken);
        return profile is not null && session.Epoch >= profile.SessionEpoch ? session.PlayerId : null;
    }

    private async Task<string> CreateMappingAsync(DocumentKey key, VerifiedIdentity identity, CancellationToken cancellationToken)
    {
        var attributes = new JsonObject();
        foreach (var (name, value) in identity.Attributes)
        {
            attributes[name] = value;
        }

        var mapping = new JsonObject
        {
            ["mappingKey"] = key.Key,
            ["playerId"] = PlayerIds.New(),
            ["gameId"] = gameServer.GameId,
            ["environmentId"] = gameServer.EnvironmentId,
            ["linkedAtUtc"] = AggregateDocumentSerializer.FormatTime(clock.UtcNow),
            ["attributes"] = attributes,
        };

        // A lost race returns the winner's mapping: both logins end on the same player (IDN-01).
        var (stored, inserted) = await claims.InsertOrGetAsync(key, mapping, cancellationToken);
        var playerId = stored["playerId"]!.GetValue<string>();
        if (inserted)
        {
            logger.LogInformation("New player {PlayerId} created by {Provider} login.", playerId, identity.Provider);
        }

        return playerId;
    }
}
