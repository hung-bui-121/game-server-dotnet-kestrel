using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>HTTP stand-in for the provider endpoints; counts calls per URL.</summary>
internal sealed class ScriptedHttp : HttpMessageHandler, IHttpClientFactory
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return await Respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Game Center verification (IDN-03, V19, research R3) with a self-made chain plus Apple's real certificate.</summary>
public sealed class GameCenterAdapterTests : IDisposable
{
    private const string Bundle = "com.example.game";
    private const string KeyUrl = "https://static.gc.apple.com/public-key/gc-test.cer";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly ScriptedHttp _http = new();
    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly RSA _intermediateKey = RSA.Create(2048);
    private readonly RSA _leafKey = RSA.Create(2048);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;
    private readonly X509Certificate2 _leaf;

    public GameCenterAdapterTests()
    {
        _root = Ca("CN=Test Root", _rootKey, null, null);
        _intermediate = Ca("CN=Test Code Signing CA", _intermediateKey, _root, _rootKey);
        _leaf = Leaf("C=US, O=Apple Inc., CN=Apple Inc.", _leafKey, _intermediate, _intermediateKey, Now.AddYears(1));
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_leaf.RawData) });
    }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        _leaf.Dispose();
        _rootKey.Dispose();
        _intermediateKey.Dispose();
        _leafKey.Dispose();
        _http.Dispose();
    }

    private static X509Certificate2 Ca(string subject, RSA key, X509Certificate2? issuer, RSA? issuerKey)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is null)
        {
            return request.CreateSelfSigned(Now.AddYears(-2), Now.AddYears(10));
        }

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var serial = RandomNumberGenerator.GetBytes(12);
        return request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey!, RSASignaturePadding.Pkcs1), Now.AddYears(-1), Now.AddYears(5), serial);
    }

    private static X509Certificate2 Leaf(string subject, RSA key, X509Certificate2 issuer, RSA issuerKey, DateTimeOffset notAfter, string eku = "1.3.6.1.5.5.7.3.3")
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1), Now.AddMonths(-1), notAfter, RandomNumberGenerator.GetBytes(12));
    }

    private GameCenterAdapter Adapter(Action<GameCenterOptions>? configure = null, bool pinned = true)
    {
        var options = new IdentityProviderOptions();
        options.GameCenter.Enabled = true;
        options.GameCenter.BundleIds = [Bundle];
        if (pinned)
        {
            options.GameCenter.TrustedRootsBase64 = [Convert.ToBase64String(_root.RawData)];
            options.GameCenter.IntermediatesBase64 = [Convert.ToBase64String(_intermediate.RawData)];
        }

        configure?.Invoke(options.GameCenter);
        return new GameCenterAdapter(_http, _clock, Options.Create(options), NullLogger<GameCenterAdapter>.Instance);
    }

    private GameCenterCredential Signed(RSA? key = null, DateTimeOffset? at = null, string url = KeyUrl, string bundle = Bundle, string team = "T:_team123")
    {
        var unsigned = new GameCenterCredential(url, [], RandomNumberGenerator.GetBytes(16), (ulong)(at ?? Now).ToUnixTimeMilliseconds(), team, "A:_game456", bundle);
        return unsigned with
        {
            Signature = (key ?? _leafKey).SignData(GameCenterAdapter.SignedData(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };
    }

    private static async Task<GameServerException> Rejected(GameCenterAdapter adapter, GameCenterCredential credential, ErrorCode code)
    {
        var ex = await Should.ThrowAsync<GameServerException>(async () => await adapter.VerifyAsync(credential, CancellationToken.None));
        ex.Code.ShouldBe(code);
        return ex;
    }

    [Fact]
    public async Task Valid_signature_yields_the_team_player_id_and_caches_the_certificate()
    {
        using var adapter = Adapter();

        var identity = await adapter.VerifyAsync(Signed(), CancellationToken.None);

        identity.Provider.ShouldBe("gamecenter");
        identity.Subject.ShouldBe("T:_team123");
        identity.MappingKey.ShouldBe("gamecenter:T:_team123");
        identity.Attributes[GameCenterAdapter.UnverifiedGamePlayerIdAttribute].ShouldBe("A:_game456");
        identity.Attributes.ShouldNotContainKey("gamePlayerId");

        await adapter.VerifyAsync(Signed(), CancellationToken.None);
        _http.Requests.Count.ShouldBe(1);

        // At most 24 h: the certificate is downloaded again after that.
        _clock.Advance(TimeSpan.FromHours(24));
        await adapter.VerifyAsync(Signed(at: _clock.UtcNow), CancellationToken.None);
        _http.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Tampered_fields_break_the_signature()
    {
        using var adapter = Adapter();
        var good = Signed();

        await Rejected(adapter, good with { Salt = RandomNumberGenerator.GetBytes(16) }, IdentityErrors.IdentityProofInvalid);
        await Rejected(adapter, good with { TeamPlayerId = "T:_someone_else" }, IdentityErrors.IdentityProofInvalid);
        await Rejected(adapter, good with { Timestamp = good.Timestamp + 1 }, IdentityErrors.IdentityProofInvalid);
        using var otherKey = RSA.Create(2048);
        await Rejected(adapter, Signed(otherKey), IdentityErrors.IdentityProofInvalid);
    }

    [Fact]
    public async Task Timestamp_must_be_within_ten_minutes()
    {
        using var adapter = Adapter();

        await adapter.VerifyAsync(Signed(at: Now.AddMinutes(-10)), CancellationToken.None);
        await adapter.VerifyAsync(Signed(at: Now.AddMinutes(10)), CancellationToken.None);
        await Rejected(adapter, Signed(at: Now.AddMinutes(-11)), IdentityErrors.IdentityProofInvalid);
        await Rejected(adapter, Signed(at: Now.AddMinutes(11)), IdentityErrors.IdentityProofInvalid);
    }

    [Fact]
    public async Task Bundle_must_be_the_games()
    {
        using var adapter = Adapter();

        await Rejected(adapter, Signed(bundle: "com.attacker.app"), IdentityErrors.IdentityProofInvalid);
    }

    [Theory]
    [InlineData("http://static.gc.apple.com/public-key/gc.cer")]
    [InlineData("https://static.gc.apple.com.evil.com/public-key/gc.cer")]
    [InlineData("https://evilapple.com/public-key/gc.cer")]
    [InlineData("https://user@static.gc.apple.com/public-key/gc.cer")]
    [InlineData("https://static.gc.apple.com:8443/public-key/gc.cer")]
    [InlineData("https://17.0.0.1/public-key/gc.cer")]
    [InlineData("https://www.apple.com/public-key/gc.cer")]
    [InlineData("https://static.gc.apple.com/redirect?to=https://internal/")]
    [InlineData("https://static.gc.apple.com/public-key/gc.cer?n=1")]
    [InlineData("https://static.gc.apple.com/public-key/gc.cer#x")]
    [InlineData("https://static.gc.apple.com/public-key/../other/gc.cer")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Public_key_url_must_be_https_on_apple_com(string url)
    {
        using var adapter = Adapter();

        await Rejected(adapter, Signed(url: url), IdentityErrors.IdentityProofInvalid);
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Certificate_must_chain_to_the_pinned_root_and_name_apple()
    {
        using var strangerKey = RSA.Create(2048);
        using var strangerRoot = Ca("CN=Stranger Root", strangerKey, null, null);
        using var stranger = Leaf("O=Apple Inc., CN=Apple Inc.", _leafKey, strangerRoot, strangerKey, Now.AddYears(1));
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stranger.RawData) });

        // A key on Apple's host that no longer matches the pins is the operator's problem: unavailable, not "bad proof".
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
        }

        using var notApple = Leaf("O=Not Apple, CN=Apple Inc.", _leafKey, _intermediate, _intermediateKey, Now.AddYears(1));
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(notApple.RawData) });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
        }

        using var serverAuth = Leaf("O=Apple Inc., CN=Apple Inc.", _leafKey, _intermediate, _intermediateKey, Now.AddYears(1), eku: "1.3.6.1.5.5.7.3.1");
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serverAuth.RawData) });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
        }

        // An expired key is an old key URL sent by the client.
        using var expired = Leaf("O=Apple Inc., CN=Apple Inc.", _leafKey, _intermediate, _intermediateKey, Now.AddDays(-1));
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expired.RawData) });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.IdentityProofInvalid);
        }
    }

    [Fact]
    public async Task Download_failures_are_classified()
    {
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.IdentityProofInvalid);
        }

        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        using (var adapter = Adapter())
        {
            (await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable)).RetryAfterMs.ShouldBe(2000);
        }

        _http.Respond = _ => throw new HttpRequestException("connection refused");
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
        }

        // Redirects are answered, not followed.
        _http.Requests.Clear();
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://internal.example/") } });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
            _http.Requests.Count.ShouldBe(1);
        }

        // Connection dropped mid-body.
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.ProviderUnavailable);
        }

        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GameCenterAdapter.MaxCertificateBytes + 1]) });
        using (var adapter = Adapter())
        {
            (await Rejected(adapter, Signed(), IdentityErrors.IdentityProofInvalid)).Message.ShouldContain("too large");
        }

        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>")) });
        using (var adapter = Adapter())
        {
            await Rejected(adapter, Signed(), IdentityErrors.IdentityProofInvalid);
        }
    }

    [Fact]
    public async Task Apples_current_certificate_chains_to_the_default_pins()
    {
        // gc-prod-12.cer (valid 2026-03-17 .. 2027-05-27) under DigiCert: chain, subject and EKU pass with the built-in pins,
        // so only the (unforgeable) signature check remains.
        var real = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gc-prod-12.cer"));
        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(real) });
        using var adapter = Adapter(pinned: false);

        var ex = await Rejected(adapter, Signed(url: "https://static.gc.apple.com/public-key/gc-prod-12.cer"), IdentityErrors.IdentityProofInvalid);

        ex.Message.ShouldContain("signature does not match");
    }

    [Fact]
    public void Signed_data_layout_matches_apple()
    {
        var credential = new GameCenterCredential(KeyUrl, [], [0xAA, 0xBB], 0x0102030405060708, "T:_1", "A:_1", "b.id");

        GameCenterAdapter.SignedData(credential).ShouldBe([.. "T:_1"u8.ToArray(), .. "b.id"u8.ToArray(), 1, 2, 3, 4, 5, 6, 7, 8, 0xAA, 0xBB]);
    }
}

/// <summary>A response body whose connection drops while reading.</summary>
internal sealed class FailingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<int>(new IOException("connection reset"));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Play Games Services v2 verification (IDN-03, V18, research R2) against a scripted Google.</summary>
public sealed class PlayGamesAdapterTests : IDisposable
{
    private readonly ScriptedHttp _http = new();

    public void Dispose() => _http.Dispose();

    private PlayGamesAdapter Adapter()
    {
        var options = new IdentityProviderOptions();
        options.PlayGames.Enabled = true;
        options.PlayGames.ClientId = "client-id.apps.googleusercontent.com";
        options.PlayGames.ClientSecret = "client-secret";
        return new PlayGamesAdapter(_http, Options.Create(options), NullLogger<PlayGamesAdapter>.Instance);
    }

    private void Google(HttpStatusCode tokenStatus = HttpStatusCode.OK, string tokenJson = """{"access_token":"at-1","token_type":"Bearer"}""",
        HttpStatusCode meStatus = HttpStatusCode.OK, string meJson = """{"kind":"games#player","playerId":"g123","gamePlayerId":"a456","originalPlayerId":"g001"}""") =>
        _http.Respond = request => Task.FromResult(request.RequestUri!.Host == "oauth2.googleapis.com"
            ? ScriptedHttp.Json(tokenJson, tokenStatus)
            : ScriptedHttp.Json(meJson, meStatus));

    private async Task<GameServerException> Rejected(string code, ErrorCode expected)
    {
        var ex = await Should.ThrowAsync<GameServerException>(async () => await Adapter().VerifyAsync(new PlayGamesCredential(code), CancellationToken.None));
        ex.Code.ShouldBe(expected);
        return ex;
    }

    [Fact]
    public async Task Code_is_exchanged_and_players_me_names_the_player()
    {
        Google();

        var identity = await Adapter().VerifyAsync(new PlayGamesCredential("4/0Ab-code"), CancellationToken.None);

        identity.MappingKey.ShouldBe("pgs:g123");
        identity.Attributes["gamePlayerId"].ShouldBe("a456");
        identity.Attributes["originalPlayerId"].ShouldBe("g001");

        _http.Requests[0].Method.ShouldBe(HttpMethod.Post);
        _http.Requests[0].RequestUri!.ToString().ShouldBe("https://oauth2.googleapis.com/token");
        _http.Bodies[0].ShouldContain("grant_type=authorization_code");
        _http.Bodies[0].ShouldContain("code=4%2F0Ab-code");
        _http.Bodies[0].ShouldContain("client_id=client-id.apps.googleusercontent.com");
        _http.Bodies[0].ShouldEndWith("&redirect_uri=");
        _http.Requests[1].RequestUri!.ToString().ShouldBe("https://games.googleapis.com/games/v1/players/me");
        _http.Requests[1].Headers.Authorization!.ToString().ShouldBe("Bearer at-1");
    }

    [Fact]
    public async Task Ids_are_optional_except_player_id()
    {
        Google(meJson: """{"playerId":"g123"}""");
        (await Adapter().VerifyAsync(new PlayGamesCredential("c"), CancellationToken.None)).Attributes.ShouldBeEmpty();

        Google(meJson: """{"displayName":"x"}""");
        await Rejected("c", IdentityErrors.IdentityProofInvalid);
    }

    [Fact]
    public async Task Refused_codes_and_tokens_are_invalid_proofs()
    {
        Google(tokenStatus: HttpStatusCode.BadRequest, tokenJson: """{"error":"invalid_grant"}""");
        await Rejected("used-code", IdentityErrors.IdentityProofInvalid);

        Google(meStatus: HttpStatusCode.Forbidden, meJson: """{"error":{"code":403}}""");
        await Rejected("c", IdentityErrors.IdentityProofInvalid);

        Google(tokenJson: """{"token_type":"Bearer"}""");
        await Rejected("c", IdentityErrors.IdentityProofInvalid);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"redirect_uri_mismatch"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""")]
    [InlineData(HttpStatusCode.BadRequest, "<html>")]
    public async Task Rejections_of_the_servers_own_client_are_not_blamed_on_the_player(HttpStatusCode status, string body)
    {
        Google(tokenStatus: status, tokenJson: body);

        await Rejected("c", IdentityErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task Google_outages_are_retryable()
    {
        Google(tokenStatus: HttpStatusCode.InternalServerError);
        (await Rejected("c", IdentityErrors.ProviderUnavailable)).RetryAfterMs.ShouldBe(2000);

        Google(meStatus: HttpStatusCode.ServiceUnavailable);
        await Rejected("c", IdentityErrors.ProviderUnavailable);

        _http.Respond = _ => throw new TaskCanceledException("timeout");
        await Rejected("c", IdentityErrors.ProviderUnavailable);

        Google(meJson: "not json");
        await Rejected("c", IdentityErrors.ProviderUnavailable);

        _http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) });
        await Rejected("c", IdentityErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task Empty_or_huge_codes_are_bad_requests()
    {
        await Rejected(string.Empty, CoreErrorCodes.BadRequest);
        await Rejected(new string('x', 513), CoreErrorCodes.BadRequest);
        _http.Requests.ShouldBeEmpty();
    }
}
