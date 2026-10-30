using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Couchbase;
using Couchbase.Core.IO.Compression;
using GameServer.Modules.Identity;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sample.Counter.Adapters;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 011 US4 without a cluster (never skipped): the <c>ClusterOptions</c> mapping (FR-011, FR-016), the settings check (FR-013,
/// FR-014, FR-015, US4-2, US4-3, US4-5, US4-6, US3-7) and the custom-root TLS validation (SEC-01).
/// </summary>
public sealed class CouchbaseSettingsTests
{
    private static CouchbaseOptions Valid() => new()
    {
        ConnectionString = "couchbase://cb1,cb2",
        Username = "gs",
        Password = "from-the-environment",
        BucketName = "sample-dev",
    };

    private static GameServerOptions Deployment(string environment = DeploymentEnvironments.Development) =>
        new() { GameId = "sample", EnvironmentId = "test", DeploymentEnvironment = environment };

    private static StartupCheckResult Check(
        CouchbaseOptions options,
        string environment = DeploymentEnvironments.Development,
        Dictionary<string, string?>? configuration = null,
        GameServerOptions? gameServer = null)
    {
        var result = new StartupCheckResult();
        new CouchbaseSettingsCheck(
                options,
                gameServer ?? Deployment(environment),
                new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(),
                Path.GetTempPath())
            .Check(result);
        return result;
    }

    [Fact]
    public void The_factory_maps_every_key()
    {
        var options = Valid();
        options.KvTimeoutMs = 3000;
        options.QueryTimeoutMs = 15000;
        options.Compression = false;
        options.CompressionMinSizeBytes = 2048;
        options.NumKvConnections = 1;
        options.MaxKvConnections = 2;
        var gameServer = Deployment();
        gameServer.Transactions.KvDurableTimeoutSeconds = 7;

        var cluster = Create(options, gameServer);

        cluster.ConnectionString.ShouldBe("couchbase://cb1,cb2");
        cluster.KvTimeout.ShouldBe(TimeSpan.FromMilliseconds(3000));
        cluster.QueryTimeout.ShouldBe(TimeSpan.FromMilliseconds(15000));
        cluster.Compression.ShouldBeFalse();
        cluster.CompressionMinSize.ShouldBe(2048);
        cluster.NumKvConnections.ShouldBe(1);
        cluster.MaxKvConnections.ShouldBe(2);
        CompressionAlgorithm(cluster).ShouldBe(NoCompression);

        // The durable commit timeout keeps its own key (GameServer:Transactions:KvDurableTimeoutSeconds).
        cluster.KvDurabilityTimeout.ShouldBe(TimeSpan.FromSeconds(7));
        cluster.TransactionsConfig.ShouldNotBeNull().MetadataCollection.ShouldNotBeNull().ToString().ShouldContain("g_sample");
    }

    [Fact]
    public void The_defaults_are_the_sdk_defaults_except_the_query_timeout_and_compression_is_real()
    {
        var cluster = Create(Valid(), Deployment());
        var sdk = new ClusterOptions();

        cluster.KvTimeout.ShouldBe(sdk.KvTimeout);
        cluster.QueryTimeout.ShouldBe(TimeSpan.FromSeconds(20));
        cluster.CompressionMinSize.ShouldBe(sdk.CompressionMinSize);
        cluster.NumKvConnections.ShouldBe(sdk.NumKvConnections);
        cluster.MaxKvConnections.ShouldBe(sdk.MaxKvConnections);

        // [VERIFY] T033: the SDK's default algorithm is a no-op, so Compression=true registers Snappy.
        cluster.Compression.ShouldBeTrue();
        CompressionAlgorithm(new ClusterOptions()).ShouldBe(NoCompression);
        CompressionAlgorithm(cluster).ShouldBe("SnappierCompression");
    }

    [Fact]
    public void A_ca_is_the_only_trusted_root_of_a_tls_connection_and_host_names_are_still_checked()
    {
        using var ca = TestCertificates.CreateCa("CN=Game server test CA");
        using var otherCa = TestCertificates.CreateCa("CN=Other CA");
        using var server = TestCertificates.CreateServer(ca, "cb1.test");
        var options = Valid();
        options.ConnectionString = "couchbases://cb1.test";

        var tls = Create(options, Deployment(), [ca]).TlsSettings;
        var validate = tls.KvCertificateValidationCallback.ShouldNotBeNull();
        tls.HttpCertificateValidationCallback.ShouldBe(validate);

        validate(this, server, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
        validate(this, server, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeFalse();
        CouchbaseTrustedRoots.IsTrusted([otherCa], server, null, SslPolicyErrors.None).ShouldBeFalse();
    }

    [Fact]
    public void Without_tls_or_without_a_ca_the_sdk_validation_is_unchanged()
    {
        using var ca = TestCertificates.CreateCa("CN=Game server test CA");
        var tlsWithoutCa = Valid();
        tlsWithoutCa.ConnectionString = "couchbases://cb1";

        Create(Valid(), Deployment(), [ca]).TlsSettings.KvCertificateValidationCallback.ShouldBeNull();
        Create(tlsWithoutCa, Deployment()).TlsSettings.KvCertificateValidationCallback.ShouldBeNull();
    }

    [Fact]
    public void Valid_settings_pass_silently()
    {
        var result = Check(Valid());

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(nameof(CouchbaseOptions.KvTimeoutMs), 99, "100..10000")]
    [InlineData(nameof(CouchbaseOptions.KvTimeoutMs), 10001, "100..10000")]
    [InlineData(nameof(CouchbaseOptions.QueryTimeoutMs), 99, "100..30000")]
    [InlineData(nameof(CouchbaseOptions.QueryTimeoutMs), 30001, "100..30000")]
    [InlineData(nameof(CouchbaseOptions.CompressionMinSizeBytes), -1, "0..20971520")]
    [InlineData(nameof(CouchbaseOptions.CompressionMinSizeBytes), 20971521, "0..20971520")]
    [InlineData(nameof(CouchbaseOptions.NumKvConnections), 0, "1..32")]
    [InlineData(nameof(CouchbaseOptions.NumKvConnections), 33, "1..32")]
    [InlineData(nameof(CouchbaseOptions.MaxKvConnections), 1, "2..64")]
    [InlineData(nameof(CouchbaseOptions.MaxKvConnections), 65, "2..64")]
    [InlineData(nameof(CouchbaseOptions.ProvisioningTimeoutSeconds), 29, "30..7200")]
    [InlineData(nameof(CouchbaseOptions.ProvisioningTimeoutSeconds), 7201, "30..7200")]
    public void Each_out_of_range_value_names_the_key_and_the_range(string key, int value, string range)
    {
        var options = Valid();
        typeof(CouchbaseOptions).GetProperty(key)!.SetValue(options, value);

        Check(options).Errors.ShouldHaveSingleItem().ShouldContain($"Couchbase:{key} {value} is out of range (allowed {range}");
    }

    [Theory]
    [InlineData(nameof(CouchbaseOptions.KvTimeoutMs), 100)]
    [InlineData(nameof(CouchbaseOptions.KvTimeoutMs), 10000)]
    [InlineData(nameof(CouchbaseOptions.QueryTimeoutMs), 30000)]
    [InlineData(nameof(CouchbaseOptions.CompressionMinSizeBytes), 0)]
    [InlineData(nameof(CouchbaseOptions.MaxKvConnections), 64)]
    [InlineData(nameof(CouchbaseOptions.ProvisioningTimeoutSeconds), 30)]
    [InlineData(nameof(CouchbaseOptions.ProvisioningTimeoutSeconds), 7200)]
    public void Bounds_are_inclusive(string key, int value)
    {
        var options = Valid();
        typeof(CouchbaseOptions).GetProperty(key)!.SetValue(options, value);

        Check(options).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Timeout_bounds_follow_the_endpoint_limits()
    {
        var gameServer = Deployment();
        gameServer.Limits.PlayerTimeoutSeconds = 5;
        var options = Valid();
        options.KvTimeoutMs = 5001;

        Check(options, gameServer: gameServer).Errors.ShouldHaveSingleItem().ShouldContain("(allowed 100..5000 = 1000 x min(GameServer:Limits:PlayerTimeoutSeconds");
    }

    [Theory]
    [InlineData(nameof(CouchbaseOptions.ConnectionString))]
    [InlineData(nameof(CouchbaseOptions.Username))]
    [InlineData(nameof(CouchbaseOptions.BucketName))]
    public void Each_missing_required_key_is_named_with_its_environment_variable(string key)
    {
        var options = Valid();
        typeof(CouchbaseOptions).GetProperty(key)!.SetValue(options, string.Empty);

        Check(options).Errors.ShouldHaveSingleItem().ShouldContain($"Couchbase:{key} is not set. Fix: set");
        Check(options).Errors.Single().ShouldContain($"Couchbase__{key}");
    }

    [Theory]
    [InlineData("http://cb1", "must start with couchbase:// or couchbases://")]
    [InlineData("cb1", "must start with couchbase:// or couchbases://")]
    [InlineData("couchbase://", "has no host after couchbase://")]
    [InlineData("couchbases://?network=external", "has no host after couchbases://")]
    [InlineData("couchbase:// , ", "has no host after couchbase://")]
    public void A_bad_scheme_or_a_missing_host_is_refused(string connectionString, string message)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        Check(options).Errors.ShouldHaveSingleItem().ShouldContain(message);
    }

    [Fact]
    public void The_connection_string_is_never_echoed()
    {
        var options = Valid();
        options.ConnectionString = "http://user:hunter2@cb1";

        Check(options).Errors.Single().ShouldNotContain("hunter2");
    }

    [Fact]
    public void Production_requires_tls_staging_warns_development_is_silent()
    {
        var production = Check(Valid(), DeploymentEnvironments.Production);
        var staging = Check(Valid(), DeploymentEnvironments.Staging);
        var development = Check(Valid(), DeploymentEnvironments.Development);

        production.Errors.ShouldHaveSingleItem().ShouldContain("uses couchbase:// (no TLS) in 'Production'. Fix: use couchbases://");
        staging.Errors.ShouldBeEmpty();
        staging.Warnings.ShouldHaveSingleItem().ShouldContain("uses couchbase:// (no TLS) in 'Staging'");
        development.Errors.ShouldBeEmpty();
        development.Warnings.ShouldBeEmpty();

        var tls = Valid();
        tls.ConnectionString = "couchbases://cb1";
        Check(tls, DeploymentEnvironments.Production).Errors.ShouldBeEmpty();
    }

    /// <summary>T055 m7: no cluster accepts an empty password, so it is required in Development and Test as well.</summary>
    [Theory]
    [InlineData(DeploymentEnvironments.Production)]
    [InlineData(DeploymentEnvironments.Staging)]
    [InlineData(DeploymentEnvironments.Development)]
    [InlineData(DeploymentEnvironments.Test)]
    public void A_missing_password_names_the_environment_variable_in_every_environment(string environment)
    {
        var options = Valid();
        options.ConnectionString = "couchbases://cb1";
        options.Password = string.Empty;

        var error = Check(options, environment).Errors.ShouldHaveSingleItem();
        error.ShouldContain($"Couchbase:Password is not set in '{environment}'");
        error.ShouldContain("Couchbase__Password");
    }

    [Fact]
    public void The_ca_file_must_exist_and_hold_a_certificate()
    {
        var missing = Valid();
        missing.ConnectionString = "couchbases://cb1";
        missing.TrustCertificatePath = "gs-missing-ca-" + Guid.NewGuid().ToString("N") + ".pem";
        Check(missing).Errors.ShouldHaveSingleItem().ShouldContain("Couchbase:TrustCertificatePath");
        Check(missing).Errors.Single().ShouldContain("is not readable");

        WithFile("not a certificate", path =>
        {
            var options = Valid();
            options.ConnectionString = "couchbases://cb1";
            options.TrustCertificatePath = path;
            Check(options).Errors.ShouldHaveSingleItem().ShouldContain("is not an X.509 certificate");
        });
    }

    /// <summary>T055 m2: a path the file system API refuses (here a NUL character) is reported, not thrown out of the check.</summary>
    [Fact]
    public void An_invalid_ca_path_is_reported_in_the_startup_report()
    {
        var options = Valid();
        options.ConnectionString = "couchbases://cb1";
        options.TrustCertificatePath = "ca\0.pem";

        Check(options).Errors.ShouldHaveSingleItem().ShouldContain("Couchbase:TrustCertificatePath");
    }

    /// <summary>
    /// T055 M3: the configured file must be the root CA. With the root, the server's intermediate completes the chain; with only the
    /// intermediate the chain is partial and refused; an intermediate the server does not send is never downloaded.
    /// </summary>
    [Fact]
    public void Only_the_root_ca_anchors_the_chain_and_missing_intermediates_are_not_fetched()
    {
        using var root = TestCertificates.CreateCa("CN=Game server test root");
        using var intermediate = TestCertificates.CreateIntermediate(root, "CN=Game server test intermediate");
        using var server = TestCertificates.CreateServer(intermediate, "cb1.test");
        using var sent = TestCertificates.ChainAsSent(server, intermediate);

        CouchbaseTrustedRoots.IsTrusted([root], server, sent, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
        CouchbaseTrustedRoots.IsTrusted([intermediate], server, sent, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
        CouchbaseTrustedRoots.IsTrusted([root], server, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();

        // The chain-element certificates disposed by the validation are copies: the roots stay usable for the next handshake.
        CouchbaseTrustedRoots.IsTrusted([root], server, sent, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
        root.RawData.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_pem_or_der_ca_is_accepted_and_ignored_without_tls_with_a_warning()
    {
        using var ca = TestCertificates.CreateCa("CN=Game server test CA");
        WithFile(ca.ExportCertificatePem(), pem =>
        {
            var options = Valid();
            options.ConnectionString = "couchbases://cb1";
            options.TrustCertificatePath = pem;
            Check(options).Errors.ShouldBeEmpty();
            CouchbaseTrustedRoots.Load(pem).ShouldHaveSingleItem().Thumbprint.ShouldBe(ca.Thumbprint);

            options.ConnectionString = "couchbase://cb1";
            var plain = Check(options);
            plain.Errors.ShouldBeEmpty();
            plain.Warnings.ShouldHaveSingleItem().ShouldContain("Couchbase:TrustCertificatePath is ignored");
        });

        var der = Path.Combine(Path.GetTempPath(), "gs-ca-" + Guid.NewGuid().ToString("N") + ".cer");
        File.WriteAllBytes(der, ca.Export(X509ContentType.Cert));
        try
        {
            CouchbaseTrustedRoots.Load(der).ShouldHaveSingleItem().Thumbprint.ShouldBe(ca.Thumbprint);
        }
        finally
        {
            File.Delete(der);
        }
    }

    public static TheoryData<string, string> LegacyKeys()
    {
        var keys = new TheoryData<string, string>();
        foreach (var (key, advice) in CouchbaseSettingsCheck.LegacyKeys)
        {
            keys.Add(key, advice);
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(LegacyKeys))]
    public void Each_legacy_key_is_a_warning_naming_its_replacement(string key, string advice)
    {
        // Case-insensitive (D12): the owner's file spells keys in camelCase.
        var result = Check(Valid(), configuration: new() { [key.ToUpperInvariant() + ":value"] = "1", [key.ToLowerInvariant()] = "1" });

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain($"legacy setting {key} is ignored: {advice}");
    }

    [Fact]
    public async Task A_host_reports_missing_couchbase_settings_in_the_startup_report_instead_of_throwing_at_registration()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => TestHost.StartAsync(
            b => b.AddModule<IdentityModule>().AddModule<CounterModule>(),
            s => s["GameServer:Store"] = nameof(StoreKind.Couchbase),
            services: s => s.AddCouchbaseDocumentStore(new CouchbaseOptions())));

        ex.Message.ShouldContain("Couchbase:ConnectionString is not set");
        ex.Message.ShouldContain("Couchbase:Username is not set");
        ex.Message.ShouldContain("Couchbase:BucketName is not set");
    }

    private static ClusterOptions Create(CouchbaseOptions options, GameServerOptions gameServer, X509Certificate2Collection? roots = null) =>
        CouchbaseClusterOptionsFactory.Create(
            options,
            gameServer,
            global::Couchbase.Core.IO.Serializers.SystemTextJsonSerializer.Create(GameServerJson.Options),
            roots,
            NullLoggerFactory.Instance);

    /// <summary>The SDK's internal no-op algorithm (compression silently off).</summary>
    private const string NoCompression = "NullCompressionAlgorithm";

    /// <summary>
    /// Type name of the algorithm the SDK will use: its service provider and both algorithms are internal, read by reflection
    /// (SDK pinned to 3.10.0).
    /// </summary>
    private static string? CompressionAlgorithm(ClusterOptions cluster)
    {
        var build = typeof(ClusterOptions).GetMethod("BuildServiceProvider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ClusterOptions.BuildServiceProvider not found: re-check the SDK mapping (T033).");
        return ((IServiceProvider)build.Invoke(cluster, null)!).GetService(typeof(ICompressionAlgorithm))?.GetType().Name;
    }

    private static void WithFile(string content, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "gs-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, content);
        try
        {
            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Throw-away CA and server certificates (no real credentials).</summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    }

    /// <summary>A CA certificate issued by <paramref name="issuer"/> (with its private key, so it can issue the server certificate).</summary>
    public static X509Certificate2 CreateIntermediate(X509Certificate2 issuer, string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.Create(issuer, now.AddHours(-2), now.AddHours(2), RandomNumberGenerator.GetBytes(8));
        return certificate.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// The chain a TLS handshake hands to the validation callback: built by the platform from the certificates the server sent
    /// (leaf and intermediate), not trusted by the OS.
    /// </summary>
    public static X509Chain ChainAsSent(X509Certificate2 server, X509Certificate2 intermediate)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.Build(server);
        return chain;
    }

    public static X509Certificate2 CreateServer(X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.Create(ca, now.AddHours(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(8));
    }
}

/// <summary>Slice 011 US4 on a real cluster (skipped with a message without <c>COUCHBASE_CONNSTR</c>): FR-011, US4-1, US4-4.</summary>
public sealed class CouchbaseSettingsIntegrationTests(CouchbaseFixture fixture) : IClassFixture<CouchbaseFixture>
{
    private const string Collection = "conf_docs";
    private const string TlsConnectionStringVariable = "COUCHBASE_TLS_CONNSTR";
    private const string CaPathVariable = "COUCHBASE_CA_PATH";

    private CouchbaseOptions With(Action<CouchbaseOptions> change)
    {
        fixture.RequireCouchbase();
        var options = new CouchbaseOptions
        {
            ConnectionString = fixture.Options.ConnectionString,
            Username = fixture.Options.Username,
            Password = fixture.Options.Password,
            BucketName = fixture.Options.BucketName,
        };
        change(options);
        return options;
    }

    private static DocumentKey NewKey() => new(Collection, "settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Every_setting_changed_from_its_default_still_reads_writes_and_commits()
    {
        var options = With(o =>
        {
            o.KvTimeoutMs = 3000;
            o.QueryTimeoutMs = 15000;
            o.Compression = false;
            o.CompressionMinSizeBytes = 2048;
            o.NumKvConnections = 1;
            o.MaxKvConnections = 2;
        });
        await using var connection = new CouchbaseConnection(options, fixture.GameServer, NullLoggerFactory.Instance);
        var store = new CouchbaseDocumentStore(connection);
        var kv = NewKey();
        var transactional = NewKey();

        (await ((IKeyValueStore)store).InsertAsync(kv, new() { ["v"] = new string('x', 4096) }, null, CancellationToken.None)).ShouldBeTrue();
        (await ((IKeyValueStore)store).GetAsync(kv, CancellationToken.None)).ShouldNotBeNull().Content["v"]!.GetValue<string>().Length.ShouldBe(4096);
        await store.RunTransactionAsync(
            async tx =>
            {
                await tx.InsertAsync(transactional, new() { ["v"] = 1 });
                return true;
            },
            new TransactionRunOptions(TimeSpan.FromSeconds(15)));
        (await ((ITransactionalDocumentStore)store).GetAsync(transactional, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_compressible_document_round_trips_with_snappy_on()
    {
        fixture.RequireCouchbase();
        var key = NewKey();
        var store = (IKeyValueStore)fixture.Store;
        var body = string.Concat(Enumerable.Repeat("compressible ", 1000));

        (await store.InsertAsync(key, new() { ["v"] = body }, null, CancellationToken.None)).ShouldBeTrue();

        fixture.Options.Compression.ShouldBeTrue();
        (await store.GetAsync(key, CancellationToken.None)).ShouldNotBeNull().Content["v"]!.GetValue<string>().ShouldBe(body);
    }

    [Fact]
    public async Task A_seed_list_with_an_unreachable_first_node_connects()
    {
        var options = With(o =>
        {
            var hosts = o.ConnectionString[CouchbaseOptions.Scheme.Length..];
            o.ConnectionString = CouchbaseOptions.Scheme + "unreachable.invalid," + hosts;
        });
        await using var connection = new CouchbaseConnection(options, fixture.GameServer, NullLoggerFactory.Instance);
        var key = NewKey();

        IKeyValueStore store = new CouchbaseDocumentStore(connection);
        (await store.InsertAsync(key, new() { ["v"] = 1 }, null, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_query_timeout_is_honoured()
    {
        // KV timeout: only the mapping is asserted (CouchbaseSettingsTests): a local node cannot delay KV reliably.
        var options = With(o => o.QueryTimeoutMs = 200);
        await using var connection = new CouchbaseConnection(options, fixture.GameServer, NullLoggerFactory.Instance);
        var cluster = await connection.GetClusterAsync();
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<global::Couchbase.Core.Exceptions.TimeoutException>(async () =>
        {
            // ~18 s of work on EE 7.6.12. A single ARRAY_RANGE(0, 100000000) fails at once instead (5037: array_range() above 20 MiB).
            var result = await cluster.QueryAsync<long>("SELECT RAW COUNT(1) FROM ARRAY_RANGE(0, 3000) AS a UNNEST ARRAY_RANGE(0, 3000) AS b");
            await foreach (var _ in result.Rows)
            {
            }
        });

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Tls_trusts_the_configured_ca_and_refuses_another()
    {
        var connectionString = Environment.GetEnvironmentVariable(TlsConnectionStringVariable);
        var caPath = Environment.GetEnvironmentVariable(CaPathVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(caPath),
            $"{TlsConnectionStringVariable} and {CaPathVariable} not set: the TLS integration test needs a cluster with TLS (opt-in, FR-014).");

        var trusted = With(o =>
        {
            o.ConnectionString = connectionString!;
            o.TrustCertificatePath = caPath;
        });
        await using (var connection = new CouchbaseConnection(trusted, fixture.GameServer, NullLoggerFactory.Instance))
        {
            var bucket = await (await connection.GetClusterAsync()).BucketAsync(trusted.BucketName);
            await bucket.WaitUntilReadyAsync(TimeSpan.FromSeconds(10));
        }

        using var foreign = TestCertificates.CreateCa("CN=Foreign CA");
        var foreignPath = Path.Combine(Path.GetTempPath(), "gs-foreign-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(foreignPath, foreign.ExportCertificatePem());
        try
        {
            var refused = With(o =>
            {
                o.ConnectionString = connectionString!;
                o.TrustCertificatePath = foreignPath;
            });
            await using var connection = new CouchbaseConnection(refused, fixture.GameServer, NullLoggerFactory.Instance);
            await Should.ThrowAsync<Exception>(async () =>
            {
                var bucket = await (await connection.GetClusterAsync()).BucketAsync(refused.BucketName);
                await bucket.WaitUntilReadyAsync(TimeSpan.FromSeconds(10));
            });
        }
        finally
        {
            File.Delete(foreignPath);
        }
    }
}
