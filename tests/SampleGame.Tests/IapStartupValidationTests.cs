using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameServer.Platform;
using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// T055 M4: the IAP settings are part of the one startup report (IAP-01, FR-009). Outside Development/Test the fake verifier is
/// refused and every key of the real verifiers must be set and parse; a Development host that opts into the real verifiers gets
/// the same checks. No message echoes a key value. The hosts run on the in-memory store, so a Production start still fails for
/// that reason; the assertions look at the IAP lines only.
/// </summary>
public sealed class IapStartupValidationTests
{
    private const string Prefix = "[module iap] ";
    private const string PackageName = "Iap:Google:PackageName";
    private const string ServiceAccountEmail = "Iap:Google:ServiceAccountEmail";
    private const string PrivateKey = "Iap:Google:ServiceAccountPrivateKeyPem";
    private const string BundleId = "Iap:Apple:BundleId";
    private const string AppleRoot = "Iap:Apple:RootCertificateBase64";
    private const int RsaKeyBits = 2048;

    private static readonly string[] Keys = [PackageName, ServiceAccountEmail, PrivateKey, BundleId, AppleRoot];

    private static async Task<string[]> IapErrorsAsync(string environment, Action<Dictionary<string, string?>> settings)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(
                modules: GameHttp.FullGame,
                settings: s =>
                {
                    s["GameServer:DeploymentEnvironment"] = environment;
                    s["Identity:GuestPepper"] = "pepper";
                    settings(s);
                },
                testAuthentication: false);
        });
        return ex.Violations.Where(v => v.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
    }

    private static void ValidIap(Dictionary<string, string?> settings)
    {
        using var rsa = RSA.Create(RsaKeyBits);
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var root = new CertificateRequest("CN=Test Apple Root", rootKey, HashAlgorithmName.SHA256).CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        settings[PackageName] = "com.example.sample";
        settings[ServiceAccountEmail] = "iap-verifier@example.invalid";
        settings[PrivateKey] = rsa.ExportPkcs8PrivateKeyPem();
        settings[BundleId] = "com.example.sample";
        settings[AppleRoot] = Convert.ToBase64String(root.RawData);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Missing_verifier_keys_are_each_named_with_their_environment_variable(string environment)
    {
        var errors = await IapErrorsAsync(environment, _ => { });

        errors.Length.ShouldBe(Keys.Length, string.Join(Environment.NewLine, errors));
        foreach (var key in Keys)
        {
            errors.ShouldContain($"{Prefix}{key} is not set. Fix: set the environment variable {key.Replace(":", "__", StringComparison.Ordinal)} (IAP-01).");
        }
    }

    [Fact]
    public async Task Valid_verifier_keys_add_no_iap_error()
    {
        (await IapErrorsAsync("Production", ValidIap)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_fake_verifier_is_refused_outside_development()
    {
        var errors = await IapErrorsAsync("Production", s =>
        {
            ValidIap(s);
            s["Iap:UseFakeVerifier"] = "true";
        });

        errors.ShouldHaveSingleItem().ShouldStartWith($"{Prefix}Iap:UseFakeVerifier is true in 'Production'.");
    }

    [Fact]
    public async Task Unreadable_key_material_is_named_without_its_value()
    {
        using var rsa = RSA.Create(RsaKeyBits);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        const string notBase64 = "not-base64-certificate-value";

        foreach (var pem in new[] { "not-a-pem-key-value", publicKey })
        {
            var errors = await IapErrorsAsync("Production", s =>
            {
                ValidIap(s);
                s[PrivateKey] = pem;
                s[AppleRoot] = notBase64;
            });

            errors.Length.ShouldBe(2, string.Join(Environment.NewLine, errors));
            errors.ShouldContain(e => e.StartsWith($"{Prefix}{PrivateKey} is not a PEM RSA private key.", StringComparison.Ordinal));
            errors.ShouldContain(e => e.StartsWith($"{Prefix}{AppleRoot} is not a base64 DER X.509 certificate.", StringComparison.Ordinal));
            var report = string.Join(Environment.NewLine, errors);
            report.ShouldNotContain("not-a-pem-key-value");
            report.ShouldNotContain("BEGIN PUBLIC KEY");
            report.ShouldNotContain(notBase64);
        }
    }

    [Fact]
    public async Task A_development_host_that_opts_into_the_real_verifiers_needs_their_keys()
    {
        var errors = await IapErrorsAsync("Development", s => s["Iap:UseFakeVerifier"] = "false");

        errors.Length.ShouldBe(Keys.Length, string.Join(Environment.NewLine, errors));
    }
}
