using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Modules.Identity;

/// <summary>
/// Login with a provider identity (IDN-03, IDN-11): verify → mapping → (switch check) → common login end. The mapped
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

    /// <summary>Player of a valid, current session sent with the login; anything else counts as no session.</summary>
    private async Task<string?> CurrentPlayerAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128 || await sessions.ValidateAsync(token, cancellationToken) is not { } session)
        {
            return null;
        }

        var profile = await profiles.GetAsync(session.PlayerId, cancellationToken);
        return session.Epoch >= profile.SessionEpoch ? session.PlayerId : null;
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
