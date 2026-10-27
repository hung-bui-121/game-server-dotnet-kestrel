using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GameServer.Gm.Tests.Infrastructure;

/// <summary>
/// In-process OpenID Connect provider for SSO tests (research R13): discovery, JWKS (RS256), an authorization step the
/// test drives as "the user signing in", and a token endpoint that checks the code and the PKCE verifier.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "gm-console";
    public const string ClientSecret = "test-client-secret";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();

    public FakeOidcProvider() => _key = new RsaSecurityKey(_rsa) { KeyId = "k1" };

    /// <summary>Token requests that failed validation (wrong code, verifier or client).</summary>
    public int RejectedTokenRequests { get; private set; }

    private sealed record PendingCode(string RedirectUri, string Nonce, string CodeChallenge, IReadOnlyDictionary<string, object> Claims);

    /// <summary>
    /// The user signs in at the provider: returns the callback URL (query response mode) the browser is sent to.
    /// <paramref name="claims"/> go into the id_token (sub, email, email_verified, amr…).
    /// </summary>
    public string Authorize(Uri authorizeUrl, IReadOnlyDictionary<string, object> claims, string? overrideState = null)
    {
        var query = QueryHelpers.ParseQuery(authorizeUrl.Query);
        if (query["client_id"] != ClientId || query["response_type"] != "code" || query["code_challenge_method"] != "S256")
        {
            throw new InvalidOperationException("Unexpected authorization request: " + authorizeUrl);
        }

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new PendingCode(query["redirect_uri"]!, query["nonce"]!, query["code_challenge"]!, claims);
        return QueryHelpers.AddQueryString(query["redirect_uri"]!, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = overrideState ?? query["state"],
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/.well-known/openid-configuration" => Json(new
            {
                issuer = Issuer,
                authorization_endpoint = Issuer + "/authorize",
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/jwks",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                code_challenge_methods_supported = new[] { "S256" },
            }),
            "/jwks" => Jwks(),
            "/token" => await TokenAsync(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
        var (clientId, clientSecret) = ClientCredentials(request, form);
        if (form["grant_type"] != "authorization_code"
            || !_codes.TryRemove(form["code"].ToString(), out var pending)
            || form["redirect_uri"] != pending.RedirectUri
            || clientId != ClientId
            || clientSecret != ClientSecret
            || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != pending.CodeChallenge)
        {
            RejectedTokenRequests++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") };
        }

        var claims = new Dictionary<string, object>(pending.Claims) { ["nonce"] = pending.Nonce };
        var now = DateTime.UtcNow;
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        return Json(new { access_token = "opaque-access-token", token_type = "Bearer", expires_in = 300, id_token = idToken });
    }

    private static (string? Id, string? Secret) ClientCredentials(HttpRequestMessage request, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form)
    {
        if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic })
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(basic)).Split(':', 2);
            return (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
        }

        return (form["client_id"], form["client_secret"]);
    }

    private HttpResponseMessage Jwks()
    {
        var key = _rsa.ExportParameters(false);
        return Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "k1", n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } });
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }
}
