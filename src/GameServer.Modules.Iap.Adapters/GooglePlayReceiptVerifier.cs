using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Platform;
using GameServer.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Iap;

/// <summary>
/// Google Play verification (IAP-01, [VERIFY] V20): service-account JWT (RS256) → OAuth access token →
/// <c>purchases.products.get(packageName, productId, token)</c>; valid when <c>purchaseState = 0</c>. Test purchases
/// (<c>purchaseType = 0</c>) are refused in Production. External reference = purchase token; <c>orderId</c> kept in the ledger.
/// </summary>
public sealed class GooglePlayReceiptVerifier(
    IHttpClientFactory httpClients,
    IOptions<IapOptions> options,
    GameServerOptions gameServer,
    IClock clock,
    ILogger<GooglePlayReceiptVerifier> logger) : IStoreReceiptVerifier, IDisposable
{
    public const string StoreName = "google";
    public const string HttpClientName = "iap-google";
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";

    private readonly GoogleOptions _options = options.Value.Google;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _accessToken;

    public string Store => StoreName;

    public async Task<VerifiedPurchase> VerifyAsync(string productId, string receiptPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(receiptPayload) || receiptPayload.Length > 4096)
        {
            return VerifiedPurchase.Invalid(StoreName, productId, "token format");
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var client = httpClients.CreateClient(HttpClientName);
        var uri = $"{_options.ApiBaseUri.TrimEnd('/')}/androidpublisher/v3/applications/{Uri.EscapeDataString(_options.PackageName)}" +
                  $"/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(receiptPayload)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StoreUnavailableException("Google Play API unreachable.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone)
            {
                return VerifiedPurchase.Invalid(StoreName, productId, $"google {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _accessToken = null;
                }

                throw new StoreUnavailableException($"Google Play API returned {(int)response.StatusCode}.");
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var purchase = json.RootElement;
            var state = purchase.TryGetProperty("purchaseState", out var s) ? s.GetInt32() : -1;
            var isTest = purchase.TryGetProperty("purchaseType", out var type) && type.GetInt32() == 0;
            if (state != 0 || (isTest && gameServer.IsProduction))
            {
                logger.LogInformation("Google purchase rejected: state {State}, test {IsTest}.", state, isTest);
                return VerifiedPurchase.Invalid(StoreName, productId, "purchase state");
            }

            var orderId = purchase.TryGetProperty("orderId", out var order) ? order.GetString() : null;
            return new VerifiedPurchase(true, StoreName, productId, receiptPayload, orderId, null);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && cached.ExpiresAt > clock.UtcNow.AddSeconds(60))
        {
            return cached.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is { } again && again.ExpiresAt > clock.UtcNow.AddSeconds(60))
            {
                return again.Token;
            }

            var client = httpClients.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = CreateAssertion(),
            });
            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(_options.TokenUri, content, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new StoreUnavailableException("Google OAuth unreachable.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new StoreUnavailableException($"Google OAuth returned {(int)response.StatusCode}.");
                }

                using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
                var expiresIn = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
                _accessToken = (accessToken, clock.UtcNow.AddSeconds(expiresIn));
                return accessToken;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>RS256 JWT assertion of the service account (RFC 7523).</summary>
    private string CreateAssertion()
    {
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _options.ServiceAccountEmail,
            scope = Scope,
            aud = _options.TokenUri,
            iat = now,
            exp = now + 3600,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_options.ServiceAccountPrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + claims + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _tokenLock.Dispose();
}
