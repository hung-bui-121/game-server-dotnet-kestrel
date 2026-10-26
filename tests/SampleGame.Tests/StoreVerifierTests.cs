using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GameServer.Modules.Iap;
using GameServer.Platform;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Real store adapters against self-made fixtures (IAP-01). Live sandbox verification against Apple/Google is a P0.5
/// manual step ([VERIFY] V20, V21).
/// </summary>
public sealed class StoreVerifierTests
{
    private const string BundleId = "com.example.sample";
    private const string LeafOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateOid = "1.2.840.113635.100.6.2.1";

    private static readonly FakeClock Clock = new();

    private static GameServerOptions Deployment(string environment) =>
        new() { GameId = "t", EnvironmentId = "t", DeploymentEnvironment = environment };

    private sealed class AppleChain : IDisposable
    {
        public AppleChain(bool appleOids = true)
        {
            var now = Clock.UtcNow;
            RootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Test Apple Root", RootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

            using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intermediateRequest = new CertificateRequest("CN=Test Apple WWDR", intermediateKey, HashAlgorithmName.SHA256);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            if (appleOids)
            {
                intermediateRequest.CertificateExtensions.Add(new X509Extension(new Oid(IntermediateOid), [0x05, 0x00], false));
            }

            using var intermediatePublic = intermediateRequest.Create(Root, now.AddDays(-1), now.AddYears(2), [1, 2, 3]);
            Intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

            LeafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafRequest = new CertificateRequest("CN=Test StoreKit Signer", LeafKey, HashAlgorithmName.SHA256);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            if (appleOids)
            {
                leafRequest.CertificateExtensions.Add(new X509Extension(new Oid(LeafOid), [0x05, 0x00], false));
            }

            Leaf = leafRequest.Create(Intermediate, now.AddDays(-1), now.AddYears(1), [4, 5, 6]);
        }

        public ECDsa RootKey { get; }

        public X509Certificate2 Root { get; }

        public X509Certificate2 Intermediate { get; }

        public ECDsa LeafKey { get; }

        public X509Certificate2 Leaf { get; }

        public string RootBase64 => Convert.ToBase64String(Root.RawData);

        public string Sign(object payload, ECDsa? signer = null)
        {
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                x5c = new[] { Leaf, Intermediate, Root }.Select(c => Convert.ToBase64String(c.RawData)).ToArray(),
            }));
            var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
            var signature = (signer ?? LeafKey).SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return header + "." + body + "." + B64(signature);
        }

        public void Dispose()
        {
            RootKey.Dispose();
            Root.Dispose();
            Intermediate.Dispose();
            LeafKey.Dispose();
            Leaf.Dispose();
        }

        private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AppleJwsReceiptVerifier Apple(string rootBase64, string environment = "Development") =>
        new(Options.Create(new IapOptions { Apple = new AppleOptions { BundleId = BundleId, RootCertificateBase64 = rootBase64 } }),
            Deployment(environment), Clock, NullLogger<AppleJwsReceiptVerifier>.Instance);

    private static object Transaction(string environment = "Sandbox", string bundleId = BundleId, string productId = "gems_100", bool revoked = false) =>
        revoked
            ? new { transactionId = "2000000123", bundleId, productId, environment, revocationDate = 1 }
            : new { transactionId = "2000000123", bundleId, productId, environment };

    [Fact]
    public async Task Apple_valid_transaction_verifies()
    {
        using var chain = new AppleChain();

        var result = await Apple(chain.RootBase64).VerifyAsync("gems_100", chain.Sign(Transaction()), CancellationToken.None);

        result.Valid.ShouldBeTrue();
        result.ExternalReference.ShouldBe("2000000123");
    }

    [Fact]
    public async Task Apple_rejects_tampering_wrong_claims_untrusted_roots_and_missing_oids()
    {
        using var chain = new AppleChain();
        var verifier = Apple(chain.RootBase64);
        var valid = chain.Sign(Transaction());
        var parts = valid.Split('.');
        var tampered = parts[0] + "." + chain.Sign(Transaction(productId: "gems_1000")).Split('.')[1] + "." + parts[2];

        (await verifier.VerifyAsync("gems_100", tampered, CancellationToken.None)).Valid.ShouldBeFalse();
        (await verifier.VerifyAsync("gems_100", chain.Sign(Transaction(bundleId: "com.other")), CancellationToken.None)).Valid.ShouldBeFalse();
        (await verifier.VerifyAsync("other_product", valid, CancellationToken.None)).Valid.ShouldBeFalse();
        (await verifier.VerifyAsync("gems_100", chain.Sign(Transaction(revoked: true)), CancellationToken.None)).Valid.ShouldBeFalse();
        (await verifier.VerifyAsync("gems_100", "not.a.jws", CancellationToken.None)).Valid.ShouldBeFalse();

        using var otherRoot = new AppleChain();
        (await Apple(otherRoot.RootBase64).VerifyAsync("gems_100", valid, CancellationToken.None)).Valid.ShouldBeFalse();

        using var noOids = new AppleChain(appleOids: false);
        (await Apple(noOids.RootBase64).VerifyAsync("gems_100", noOids.Sign(Transaction()), CancellationToken.None)).Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task Apple_sandbox_receipt_is_refused_in_production()
    {
        using var chain = new AppleChain();

        (await Apple(chain.RootBase64, "Production").VerifyAsync("gems_100", chain.Sign(Transaction("Sandbox")), CancellationToken.None)).Valid.ShouldBeFalse();
        (await Apple(chain.RootBase64, "Production").VerifyAsync("gems_100", chain.Sign(Transaction("Production")), CancellationToken.None)).Valid.ShouldBeTrue();
    }

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static GooglePlayReceiptVerifier Google(StubHttp http, string environment = "Development")
    {
        using var rsa = RSA.Create(2048);
        var options = new IapOptions
        {
            Google = new GoogleOptions
            {
                PackageName = "com.example.sample",
                ServiceAccountEmail = "verifier@example.iam.gserviceaccount.com",
                ServiceAccountPrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
                TokenUri = "https://oauth.test/token",
                ApiBaseUri = "https://play.test",
            },
        };
        return new GooglePlayReceiptVerifier(http, Options.Create(options), Deployment(environment), Clock, NullLogger<GooglePlayReceiptVerifier>.Instance);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Play(HttpStatusCode status, object purchase) => request =>
        request.RequestUri!.Host == "oauth.test"
            ? Json(HttpStatusCode.OK, new { access_token = "at", expires_in = 3600 })
            : Json(status, purchase);

    [Fact]
    public async Task Google_purchased_state_verifies_and_token_is_cached()
    {
        using var http = new StubHttp(Play(HttpStatusCode.OK, new { purchaseState = 0, orderId = "GPA.1234" }));
        using var verifier = Google(http);

        var first = await verifier.VerifyAsync("gems_100", "purchase-token-1", CancellationToken.None);
        await verifier.VerifyAsync("gems_100", "purchase-token-2", CancellationToken.None);

        first.Valid.ShouldBeTrue();
        first.ExternalReference.ShouldBe("purchase-token-1");
        first.OrderId.ShouldBe("GPA.1234");
        http.Requests.Count(r => r.RequestUri!.Host == "oauth.test").ShouldBe(1);
        var api = http.Requests.First(r => r.RequestUri!.Host == "play.test");
        api.RequestUri!.AbsolutePath.ShouldBe("/androidpublisher/v3/applications/com.example.sample/purchases/products/gems_100/tokens/purchase-token-1");
        api.Headers.Authorization!.Parameter.ShouldBe("at");
    }

    [Fact]
    public async Task Google_cancelled_unknown_and_production_test_purchases_are_invalid()
    {
        using (var cancelled = new StubHttp(Play(HttpStatusCode.OK, new { purchaseState = 1 })))
        using (var verifier = Google(cancelled))
        {
            (await verifier.VerifyAsync("gems_100", "t", CancellationToken.None)).Valid.ShouldBeFalse();
        }

        using (var missing = new StubHttp(Play(HttpStatusCode.NotFound, new { })))
        using (var verifier = Google(missing))
        {
            (await verifier.VerifyAsync("gems_100", "t", CancellationToken.None)).Valid.ShouldBeFalse();
        }

        using var testPurchase = new StubHttp(Play(HttpStatusCode.OK, new { purchaseState = 0, purchaseType = 0 }));
        using var production = Google(testPurchase, "Production");
        (await production.VerifyAsync("gems_100", "t", CancellationToken.None)).Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task Google_server_errors_are_store_unavailable()
    {
        using var http = new StubHttp(Play(HttpStatusCode.InternalServerError, new { }));
        using var verifier = Google(http);

        await Should.ThrowAsync<StoreUnavailableException>(() => verifier.VerifyAsync("gems_100", "t", CancellationToken.None));
    }
}
