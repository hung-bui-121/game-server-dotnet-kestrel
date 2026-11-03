using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Modules.Gm;
using GameServer.Platform.Config;
using GameServer.Platform.Observability;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Config;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// FR-050, FR-051, FR-052, FR-054, FR-056, SC-005 (slice 015 T048, T050; research R12, contracts/wire-changes.md §5, data-model
/// §4.2): <c>GetConfig</c> is compressed per request for clients that ask, never for <c>not_modified</c>, small or incompressible
/// bodies, never on another route; the rollback switch sends today's bytes. The budget section is opt-in (<c>GAMESERVER_PERF=1</c>).
/// </summary>
public sealed class ConfigCompressionTests(ITestOutputHelper output)
{
    private const string ConfigRoute = "/api/v1/config/get";
    private const string AcceptEncoding = "Accept-Encoding";
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure GetConfig compression (SC-005: gzip <= 30 %, p95 <= 20 ms at 1 MiB).";

    /// <summary>The rows of contracts/wire-changes.md §5 plus case, empty and malformed values (VERIFY V7).</summary>
    public static TheoryData<string?, string?> Negotiation() => new()
    {
        { null, null },
        { "identity", null },
        { "gzip", "gzip" },
        { "br", "br" },
        { "gzip, br", "br" },
        { "br, gzip", "br" },
        { "br;q=0.5, gzip", "gzip" },
        { "gzip;q=0", null },
        { "br;q=0, gzip;q=0", null },
        { "*", "br" },
        { "*;q=0.1, gzip;q=0.5", "gzip" },
        { "deflate", null },
        { "deflate, gzip", "gzip" },
        { "GZIP", "gzip" },
        { "", null },
        { "gzip;q=abc", null },
        { ";;", null },
    };

    [Theory]
    [MemberData(nameof(Negotiation))]
    public async Task Each_accept_encoding_row_gets_its_encoding_and_the_decoded_body_is_the_uncompressed_body(string? acceptEncoding, string? expected)
    {
        await using var host = await SampleTestHost.StartAsync();
        var client = host.PlayerClient("p_negotiation");
        var plain = await (await GetConfigAsync(client, acceptEncoding: null)).Content.ReadAsByteArrayAsync();

        var response = await GetConfigAsync(client, acceptEncoding);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsByteArrayAsync();
        response.Content.Headers.ContentEncoding.ShouldBe(expected is null ? [] : [expected]);
        response.Headers.Vary.ShouldBe([AcceptEncoding]);
        response.Content.Headers.ContentLength.ShouldBe(body.Length);
        Decode(expected, body).ShouldBe(plain);
        if (expected is not null)
        {
            body.Length.ShouldBeLessThan(plain.Length);
        }
    }

    [Fact]
    public async Task Not_modified_is_never_compressed_and_carries_vary()
    {
        await using var host = await SampleTestHost.StartAsync();
        var config = host.Services.GetRequiredService<IGameConfig>();

        var response = await GetConfigAsync(host.PlayerClient("p_not_modified"), "gzip, br", new GetConfigRequest { ConfigVersion = config.ClientConfigVersion });

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldBe([AcceptEncoding]);
        GetConfigResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync()).NotModified.ShouldBeTrue();
    }

    [Fact]
    public async Task A_bundle_under_1024_bytes_is_never_compressed()
    {
        using var seeded = new SeededConfig().Manifest(m =>
        {
            m["clientVisible"] = new JsonArray("counter.json");
            return m;
        });
        await using var host = await SampleTestHost.StartAsync(settings: seeded.Apply);

        var response = await GetConfigAsync(host.PlayerClient("p_small"), "gzip, br");
        var body = await response.Content.ReadAsByteArrayAsync();

        body.Length.ShouldBeLessThan(ConfigResponseCompression.MinCompressBytes);
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldBe([AcceptEncoding]);
        GetConfigResponse.Parser.ParseFrom(body).Files.Select(f => f.Name).ShouldBe(["counter.json"]);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task An_incompressible_bundle_is_sent_uncompressed(string acceptEncoding)
    {
        using var seeded = new SeededConfig().Manifest(m =>
        {
            m["files"]!.AsArray().Add("noise.bin");
            m["clientVisible"] = new JsonArray("noise.bin");
            return m;
        });
        File.WriteAllBytes(Path.Combine(seeded.BasePath, "noise.bin"), RandomNumberGenerator.GetBytes(16 * 1024));
        await using var host = await SampleTestHost.StartAsync(settings: seeded.Apply);
        var bundle = host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle;

        var response = await GetConfigAsync(host.PlayerClient("p_noise"), acceptEncoding);

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldBe([AcceptEncoding]);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bundle.Full(host.Clock.UtcNow.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task The_rollback_switch_sends_todays_bytes_without_content_encoding_or_vary()
    {
        await using var host = await SampleTestHost.StartAsync(settings: s => s["GameServer:ConfigCompression"] = "false");
        var bundle = host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle;
        var config = host.Services.GetRequiredService<IGameConfig>();
        var client = host.PlayerClient("p_switch");

        var full = await GetConfigAsync(client, "gzip, br");
        var notModified = await GetConfigAsync(client, "gzip, br", new GetConfigRequest { ConfigVersion = config.ConfigVersion });

        foreach (var response in new[] { full, notModified })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentEncoding.ShouldBeEmpty();
            response.Headers.Vary.ShouldBeEmpty();
        }

        (await full.Content.ReadAsByteArrayAsync()).ShouldBe(bundle.Full(host.Clock.UtcNow.ToUnixTimeMilliseconds()));
        GetConfigResponse.Parser.ParseFrom(await notModified.Content.ReadAsByteArrayAsync()).NotModified.ShouldBeTrue();
    }

    [Fact]
    public async Task A_request_with_content_encoding_is_processed_as_today()
    {
        await using var host = await SampleTestHost.StartAsync();
        var config = host.Services.GetRequiredService<IGameConfig>();
        using var request = new HttpRequestMessage(HttpMethod.Post, ConfigRoute)
        {
            Content = new ByteArrayContent(new GetConfigRequest { ConfigVersion = config.ClientConfigVersion }.ToByteArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufHttp.ContentType);
        request.Content.Headers.ContentEncoding.Add("gzip");

        var response = await host.PlayerClient("p_request_encoding").SendAsync(request);

        // The body is read as sent (request bodies are never decompressed): it names the current version.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        GetConfigResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync()).NotModified.ShouldBeTrue();
    }

    [Fact]
    public async Task No_route_but_get_config_ever_sends_a_content_encoding()
    {
        await using var host = await SampleTestHost.StartAsync(
            b => GameHttp.FullGame(b.AddModule<GmModule>().AddObservability()),
            s => s["Observability:Logging:FileEnabled"] = "false");
        var catalog = host.Services.GetRequiredService<WireCatalog>();
        var client = host.PlayerClient("p_sweep");
        var swept = new List<string>();

        foreach (var route in catalog.Routes)
        {
            var path = System.Text.RegularExpressions.Regex.Replace(route.Declaration.Template, "\\{[^}]+\\}", "x");
            foreach (var method in route.Declaration.Methods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = new ByteArrayContent([]);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufHttp.ContentType);
                    request.Headers.Add("X-Request-Id", ProtobufHttp.NewRequestId());
                }

                request.Headers.TryAddWithoutValidation(AcceptEncoding, "gzip, br");
                var response = await client.SendAsync(request);
                var encoding = response.Content.Headers.ContentEncoding.ToArray();
                if (route.Declaration.Name == "platform.config")
                {
                    encoding.ShouldBe(["br"], $"{method} {path} ({(int)response.StatusCode})");
                }
                else
                {
                    encoding.ShouldBeEmpty($"{method} {path} ({route.Declaration.Name}, {(int)response.StatusCode})");
                }

                swept.Add(route.Declaration.Name);
            }
        }

        foreach (var probe in new[] { ObservabilityModule.LiveRoute, ObservabilityModule.ReadyRoute, "/v1/time" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probe);
            request.Headers.TryAddWithoutValidation(AcceptEncoding, "gzip, br");
            var response = await client.SendAsync(request);
            response.Content.Headers.ContentEncoding.ShouldBeEmpty(probe);
        }

        swept.ShouldContain("platform.config");
        swept.ShouldContain("platform.time");
        swept.Count.ShouldBeGreaterThan(20);
        output.WriteLine($"swept {swept.Count} route methods of {catalog.Routes.Count} declarations plus the probes");
    }

    /// <summary>SC-005 (V6 final): the per-request work of <see cref="ConfigResponseCompression"/> at the shipped levels.</summary>
    [Fact]
    public void Compression_of_the_sample_the_first_consumer_shaped_and_the_1_mib_bundles_stays_within_the_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        const double BudgetMs = 20;
        const double MaxGzipRatio = 0.30;
        var bundles = new (string Name, ConfigBundle Bundle)[]
        {
            ("sample", ConfigBundle.Build(TestGameConfig.FromDirectory(Path.Combine(RepositoryRoot(), "samples", "SampleHost", "config")))),
            ("first-consumer-shaped", ConfigBundle.Build(Synthetic(7, 28_000, seed: 1))),
            ("1 MiB", ConfigBundle.Build(Synthetic(12, (1024 * 1024) - 2_000, seed: 2))),
        };

        var report = new StringBuilder($"levels gzip {ConfigResponseCompression.GzipLevel}, br {ConfigResponseCompression.BrQuality}\n");
        var failures = new List<string>();
        foreach (var (name, bundle) in bundles)
        {
            var tail = bundle.Tail(1_792_972_800_000);
            var length = bundle.Prefix.Length + tail.Length;
            foreach (var encoding in new[] { ConfigResponseCompression.Gzip, ConfigResponseCompression.Brotli })
            {
                var (p50, p95, written) = Measure(encoding, bundle, tail);
                var ratio = (double)written / length;
                report.Append(System.Globalization.CultureInfo.InvariantCulture, $"{name} {length} B {encoding}: p50 {p50:F3} ms, p95 {p95:F3} ms, ratio {ratio:P1}\n");
                if (encoding == ConfigResponseCompression.Gzip && ratio > MaxGzipRatio)
                {
                    failures.Add($"{name}: gzip ratio {ratio:P1} > {MaxGzipRatio:P0}");
                }

                if (length >= 1024 * 1000 && p95 > BudgetMs)
                {
                    failures.Add($"{name} {encoding}: p95 {p95:F1} ms > {BudgetMs} ms");
                }
            }
        }

        output.WriteLine(report.ToString());
        failures.ShouldBeEmpty(report.ToString());
    }

    private static (double P50, double P95, int Written) Measure(string encoding, ConfigBundle bundle, byte[] tail)
    {
        const int WarmUp = 20;
        const int Iterations = 200;
        var length = bundle.Prefix.Length + tail.Length;
        var samples = new double[Iterations];
        var written = 0;
        for (var i = -WarmUp; i < Iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            written = ConfigResponseCompression.Compress(encoding, bundle.Prefix.Span, tail, buffer, length - 1);
            ArrayPool<byte>.Shared.Return(buffer);
            if (i >= 0)
            {
                samples[i] = watch.Elapsed.TotalMilliseconds;
            }
        }

        written.ShouldBeGreaterThan(0);
        Array.Sort(samples);
        return (samples[(Iterations / 2) - 1], samples[(int)Math.Ceiling(Iterations * 0.95) - 1], written);
    }

    private static async Task<HttpResponseMessage> GetConfigAsync(HttpClient client, string? acceptEncoding, GetConfigRequest? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ConfigRoute) { Content = new ByteArrayContent((body ?? new GetConfigRequest()).ToByteArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufHttp.ContentType);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation(AcceptEncoding, acceptEncoding);
        }

        return await client.SendAsync(request);
    }

    private static byte[] Decode(string? encoding, byte[] body)
    {
        if (encoding is null)
        {
            return body;
        }

        using var input = new MemoryStream(body);
        using Stream decoder = encoding == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        decoder.CopyTo(decoded);
        return decoded.ToArray();
    }

    /// <summary>
    /// JSON row tables shaped like game balance tables (ids, ints, decimals, enums, short strings), the generator of T006: 7 tables of
    /// about 28 KB stand for the first consumer's bundle (from the friction-log figures), 12 tables for the 1 MiB maximum.
    /// </summary>
    private static IGameConfig Synthetic(int tables, int totalBytes, int seed)
    {
        var random = new Random(seed);
        var kinds = new[] { "COMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC" };
        var perTable = totalBytes / tables;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var t = 0; t < tables; t++)
        {
            var text = new StringBuilder("{\n  \"rows\": [\n");
            for (var row = 0; text.Length < perTable - 200; row++)
            {
                text.Append(row > 0 ? ",\n" : string.Empty)
                    .Append(System.Globalization.CultureInfo.InvariantCulture, $"    {{\"id\": \"T{t}_{row}\", \"level\": {random.Next(1, 200)}, \"cost\": {random.Next(0, 1_000_000)}, ")
                    .Append(System.Globalization.CultureInfo.InvariantCulture, $"\"rate\": {random.NextDouble() * 10:F3}, \"rarity\": \"{kinds[random.Next(kinds.Length)]}\", ")
                    .Append(System.Globalization.CultureInfo.InvariantCulture, $"\"reward\": {{\"resource\": \"{(random.Next(2) == 0 ? "CASH" : "GOLD")}\", \"quantity\": {random.Next(1, 5000)}}}}}");
            }

            text.Append("\n  ]\n}\n");
            files[$"table{t}.json"] = Encoding.UTF8.GetBytes(text.ToString());
        }

        return new GameConfig("perf-1", files, files.Keys);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"GameServer.slnx not found above {AppContext.BaseDirectory}.");
    }
}
