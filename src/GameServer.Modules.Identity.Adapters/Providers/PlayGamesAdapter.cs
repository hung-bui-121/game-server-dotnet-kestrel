using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GameServer.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Google Play Games Services v2 (IDN-03, V18 / research R2): exchanges the single-use server auth code at Google OAuth
/// with the game's web client id/secret, then reads <c>games/v1/players/me</c>. The subject is <c>playerId</c> (stable
/// per game); <c>gamePlayerId</c> and <c>originalPlayerId</c> (present only after a remap) go to the attributes.
/// Only the player's proof failing (<c>invalid_grant</c>: reused or expired code; 401/403 from <c>players/me</c>) is
/// IDENTITY_PROOF_INVALID. Google rejecting the server's own OAuth client (<c>invalid_client</c>,
/// <c>unauthorized_client</c>, <c>redirect_uri_mismatch</c>…, e.g. a wrong secret after rotation) is logged as an error
/// and answered PROVIDER_UNAVAILABLE, like no answer, a timeout or 5xx — players are not blamed or rate limited for it.
/// </summary>
public sealed class PlayGamesAdapter(IHttpClientFactory httpClients, IOptions<IdentityProviderOptions> options, ILogger<PlayGamesAdapter> logger)
    : IIdentityProviderAdapter
{
    private const int MaxCodeLength = 512;
    private const int MaxResponseBytes = 64 * 1024;

    private readonly PlayGamesOptions _settings = options.Value.PlayGames;

    public string Provider => PlayGamesCredential.ProviderName;

    public async ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential credential, CancellationToken cancellationToken)
    {
        if (credential is not PlayGamesCredential { ServerAuthCode: { Length: > 0 and <= MaxCodeLength } code })
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"play_games.server_auth_code must be 1..{MaxCodeLength} characters.");
        }

        var client = httpClients.CreateClient(IdentityProviders.HttpClientName);
        using var token = await SendAsync(
            client,
            isTokenExchange: true,
            new HttpRequestMessage(HttpMethod.Post, _settings.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = _settings.ClientId,
                    ["client_secret"] = _settings.ClientSecret,
                    ["redirect_uri"] = string.Empty,
                }),
            },
            "token exchange",
            cancellationToken);
        var accessToken = token.RootElement.TryGetProperty("access_token", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new GameServerException(IdentityErrors.IdentityProofInvalid, "Google token response has no access_token.");

        var me = new HttpRequestMessage(HttpMethod.Get, _settings.PlayersMeUri);
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var player = await SendAsync(client, isTokenExchange: false, me, "players/me", cancellationToken);
        var root = player.RootElement;
        var playerId = String(root, "playerId")
            ?? throw new GameServerException(IdentityErrors.IdentityProofInvalid, "players/me has no playerId.");

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (String(root, "gamePlayerId") is { } gamePlayerId)
        {
            attributes["gamePlayerId"] = gamePlayerId;
        }

        if (String(root, "originalPlayerId") is { } originalPlayerId)
        {
            attributes["originalPlayerId"] = originalPlayerId;
        }

        return new VerifiedIdentity(Provider, playerId, attributes);
    }

    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private async Task<JsonDocument> SendAsync(HttpClient client, bool isTokenExchange, HttpRequestMessage request, string step, CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Play Games {Step} failed to answer.", step);
                throw Unavailable(step);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (!isTokenExchange)
                    {
                        // The code was good but the token cannot read the player (e.g. requested without games_lite).
                        logger.LogInformation("Play Games {Step} refused the token with {Status}.", step, (int)response.StatusCode);
                        throw new GameServerException(IdentityErrors.IdentityProofInvalid, $"Play Games {step} refused the token ({(int)response.StatusCode}).");
                    }

                    var error = await ReadOAuthErrorAsync(response, cancellationToken);
                    if (error == "invalid_grant")
                    {
                        logger.LogInformation("Play Games code rejected (invalid_grant).");
                        throw new GameServerException(IdentityErrors.IdentityProofInvalid, "Play Games code is invalid, expired or already used.");
                    }

                    logger.LogError("Play Games {Step} rejected this server's OAuth client ({Status}, {Error}). Check Identity:Providers:PlayGames.", step, (int)response.StatusCode, error ?? "no error field");
                    throw Unavailable(step);
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Play Games {Step} answered {Status}.", step, (int)response.StatusCode);
                    throw Unavailable(step);
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var buffer = new MemoryStream();
                    await CopyLimitedAsync(stream, buffer, cancellationToken);
                    return JsonDocument.Parse(buffer.ToArray());
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Play Games {Step} sent an unreadable answer.", step);
                    throw Unavailable(step);
                }
            }
        }
    }

    /// <summary>The OAuth <c>error</c> field of a refusal (bounded read), or null.</summary>
    private static async Task<string?> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new MemoryStream();
            await CopyLimitedAsync(stream, buffer, cancellationToken);
            using var document = JsonDocument.Parse(buffer.ToArray());
            return String(document.RootElement, "error");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async Task CopyLimitedAsync(Stream source, MemoryStream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("Response too large.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static GameServerException Unavailable(string step) =>
        new(IdentityErrors.ProviderUnavailable, $"Play Games {step} is unavailable.") { RetryAfterMs = 2000 };
}
