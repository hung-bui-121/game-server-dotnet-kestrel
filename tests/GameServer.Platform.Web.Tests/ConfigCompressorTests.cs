using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using GameServer.Config;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Config;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 015 T071 M3: the startup probe (an encoding that does not shrink the bundle is never offered), negotiation over the offered
/// encodings only, and the gate of the host (no free permit → the identity body). Host-level rows stay in <see cref="ConfigCompressionTests"/>.
/// </summary>
public sealed class ConfigCompressorTests : IDisposable
{
    private const long ServerTime = 1_792_972_800_000;

    private const string Skipped = "config_compression_skipped_total";

    private readonly PlatformMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    public static TheoryData<string?, bool, bool, string?> Offers() => new()
    {
        { "gzip, br", true, true, "br" },
        { "br, gzip", true, true, "br" },
        { "br;q=0.5, gzip", true, true, "gzip" },
        { "*", true, true, "br" },
        { "gzip;q=0", true, true, null },
        { "br, gzip", false, true, "gzip" },
        { "br", false, true, null },
        { "*", false, true, "gzip" },
        { "gzip", true, false, null },
        { "gzip, br", true, false, "br" },
        { "gzip, br", false, false, null },
        { null, false, false, null },
    };

    [Theory]
    [MemberData(nameof(Offers))]
    public void Negotiation_considers_only_the_offered_encodings(string? acceptEncoding, bool offerBrotli, bool offerGzip, string? expected)
    {
        var context = new DefaultHttpContext();
        if (acceptEncoding is not null)
        {
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        }

        ConfigResponseCompression.Negotiate(context.Request, offerBrotli, offerGzip).ShouldBe(expected);
    }

    [Fact]
    public async Task An_incompressible_bundle_is_offered_neither_encoding_and_sent_as_identity()
    {
        var bundle = Bundle(RandomNumberGenerator.GetBytes(8 * 1024));

        var compressor = ConfigCompressor.Create(bundle, enabled: true, new SemaphoreSlim(1), _metrics);
        var response = await WriteFullAsync(compressor, bundle, "gzip, br");

        compressor.OffersBrotli.ShouldBeFalse();
        compressor.OffersGzip.ShouldBeFalse();
        response.Encoding.ShouldBeNull();
        response.Vary.ShouldBe(HeaderNames.AcceptEncoding);
        response.Body.ShouldBe(bundle.Full(ServerTime));
        response.Length.ShouldBe(response.Body.Length);
    }

    [Fact]
    public void A_compressible_bundle_is_offered_both_encodings_and_a_small_or_disabled_one_none()
    {
        var compressible = Bundle(Compressible(4 * 1024));
        var small = Bundle(Compressible(200));

        var both = ConfigCompressor.Create(compressible, enabled: true, new SemaphoreSlim(1), _metrics);
        var under = ConfigCompressor.Create(small, enabled: true, new SemaphoreSlim(1), _metrics);
        var off = ConfigCompressor.Create(compressible, enabled: false, new SemaphoreSlim(1), _metrics);

        (both.OffersBrotli, both.OffersGzip).ShouldBe((true, true));
        (under.OffersBrotli, under.OffersGzip).ShouldBe((false, false));
        (off.Enabled, off.OffersBrotli, off.OffersGzip).ShouldBe((false, false, false));
    }

    [Fact]
    public async Task With_no_free_permit_the_response_is_identity_and_with_one_it_is_compressed()
    {
        var bundle = Bundle(Compressible(4 * 1024));
        var gate = new SemaphoreSlim(1);
        var compressor = ConfigCompressor.Create(bundle, enabled: true, gate, _metrics);
        gate.Wait(0).ShouldBeTrue();

        var busy = await WriteFullAsync(compressor, bundle, "gzip");
        gate.Release();
        var free = await WriteFullAsync(compressor, bundle, "gzip");

        busy.Encoding.ShouldBeNull();
        busy.Vary.ShouldBe(HeaderNames.AcceptEncoding);
        busy.Body.ShouldBe(bundle.Full(ServerTime));
        free.Encoding.ShouldBe("gzip");
        free.Vary.ShouldBe(HeaderNames.AcceptEncoding);
        free.Body.Length.ShouldBeLessThan(bundle.Full(ServerTime).Length);
        gate.CurrentCount.ShouldBe(1);
    }

    [Fact]
    public void A_gate_without_a_permit_is_refused()
    {
        var gate = new SemaphoreSlim(0);

        Should.Throw<ArgumentOutOfRangeException>(() => ConfigCompressor.Create(Bundle(Compressible(4 * 1024)), enabled: true, gate, _metrics)).ParamName.ShouldBe("gate");
    }

    [Fact]
    public void The_permits_are_a_quarter_of_the_processors_and_at_least_one()
    {
        ConfigCompressor.MaxConcurrentCompressions.ShouldBe(Math.Max(1, Environment.ProcessorCount / 4));
    }

    /// <summary>
    /// T071 round 2 m-2: a fallback of the gate or of the probe is counted with its reason; a compressed response and one whose
    /// client accepts no encoding are not. Only this class takes a permit by hand, so the busy count is exact; the host-level
    /// incompressible rows of <see cref="ConfigCompressionTests"/> may add probe counts in parallel.
    /// </summary>
    [Fact]
    public async Task A_fallback_of_the_gate_or_the_probe_is_counted_with_its_reason()
    {
        using var skipped = new SkipCounter();
        var compressible = Bundle(Compressible(4 * 1024));
        var incompressible = Bundle(RandomNumberGenerator.GetBytes(8 * 1024));
        var gate = new SemaphoreSlim(1);
        var compressor = ConfigCompressor.Create(compressible, enabled: true, gate, _metrics);
        var dropped = ConfigCompressor.Create(incompressible, enabled: true, new SemaphoreSlim(1), _metrics);

        (await WriteFullAsync(compressor, compressible, "gzip")).Encoding.ShouldBe("gzip");
        (await WriteFullAsync(compressor, compressible, "identity")).Encoding.ShouldBeNull();
        skipped.Count(ConfigCompressor.SkippedBusy).ShouldBe(0);
        gate.Wait(0).ShouldBeTrue();
        (await WriteFullAsync(compressor, compressible, "gzip")).Encoding.ShouldBeNull();
        gate.Release();
        var probeBefore = skipped.Count(ConfigCompressor.SkippedProbe);
        (await WriteFullAsync(dropped, incompressible, "gzip, br")).Encoding.ShouldBeNull();

        skipped.Count(ConfigCompressor.SkippedBusy).ShouldBe(1);
        skipped.Count(ConfigCompressor.SkippedProbe).ShouldBeGreaterThan(probeBefore);
    }

    /// <summary>T071 round 2 m-4: the response buffer is rented inside the try that releases the permit, so a failure there keeps the gate whole.</summary>
    [Fact]
    public async Task A_failure_while_compressing_gives_the_permit_back()
    {
        var bundle = Bundle(Compressible(4 * 1024));
        var gate = new SemaphoreSlim(1);
        var pool = new FailingPool();
        var compressor = ConfigCompressor.Create(bundle, enabled: true, gate, _metrics, pool);
        pool.Fail = true;

        await Should.ThrowAsync<InsufficientMemoryException>(() => WriteFullAsync(compressor, bundle, "gzip"));

        gate.CurrentCount.ShouldBe(1);
        pool.Fail = false;
        (await WriteFullAsync(compressor, bundle, "gzip")).Encoding.ShouldBe("gzip");
    }

    /// <summary>
    /// T071 round 2 m-4: a bundle over <c>GameServer:Limits:ConfigBundleBytes</c> stops the start, so the config phase creates its
    /// compressor switched off without probing; at the limit the same bundle is probed and offered both encodings.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void An_over_limit_bundle_is_not_probed(int overLargest, bool probed)
    {
        var largest = Phase(_ => { }).Bundle!.MaxResponseBytes;

        var summary = Phase(s => s["GameServer:Limits:ConfigBundleBytes"] = (largest + overLargest).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var compressor = summary.Compressor.ShouldNotBeNull();
        (compressor.Enabled, compressor.OffersBrotli, compressor.OffersGzip).ShouldBe((probed, probed, probed));
    }

    [Fact]
    public void The_description_names_the_kept_encodings_and_the_permits()
    {
        var compressible = Bundle(Compressible(4 * 1024));

        ConfigCompressor.Create(compressible, enabled: true, new SemaphoreSlim(2), _metrics).Describe().ShouldBe("br,gzip, permits 2");
        ConfigCompressor.Create(Bundle(RandomNumberGenerator.GetBytes(8 * 1024)), enabled: true, new SemaphoreSlim(1), _metrics).Describe().ShouldBe("none, permits 1");
        ConfigCompressor.Create(compressible, enabled: false, new SemaphoreSlim(1), _metrics).Describe().ShouldBe("off");
    }

    /// <summary>Runs the config phase of a freshly built sample host, as the first block of <c>UseGameServer</c> does.</summary>
    private static ConfigPhaseSummary.Values Phase(Action<Dictionary<string, string?>> settings)
    {
        var app = SampleTestHost.BuildApp(settings: settings);
        try
        {
            var services = app.Services;
            ConfigStartup.Run(services, services.GetRequiredService<GameServerOptions>(), services.GetRequiredService<ModuleRegistry>(), []);
            return services.GetRequiredService<ConfigPhaseSummary>().Current;
        }
        finally
        {
            ((IDisposable)app).Dispose();
        }
    }

    private static ConfigBundle Bundle(byte[] visible) =>
        ConfigBundle.Build(new GameConfig("test", new Dictionary<string, byte[]> { ["data.bin"] = visible, ["server.json"] = "{}"u8.ToArray() }, ["data.bin"]));

    private static byte[] Compressible(int bytes)
    {
        var text = new StringBuilder();
        for (var row = 0; text.Length < bytes; row++)
        {
            text.Append("{\"id\": \"item_").Append(row).Append("\", \"kind\": \"COMMON\", \"price\": 100},\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString(0, bytes));
    }

    private static async Task<(string? Encoding, string? Vary, long? Length, byte[] Body)> WriteFullAsync(
        ConfigCompressor compressor, ConfigBundle bundle, string acceptEncoding)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        using var body = new MemoryStream();
        context.Response.Body = body;

        await compressor.WriteFullAsync(context, bundle, bundle.Tail(ServerTime));

        var encoding = context.Response.Headers.ContentEncoding;
        return (encoding.Count == 0 ? null : encoding.ToString(), context.Response.Headers.Vary.ToString(), context.Response.ContentLength, body.ToArray());
    }

    /// <summary>Reasons of <c>config_compression_skipped_total</c> recorded while it lives.</summary>
    private sealed class SkipCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string?> _reasons = new();

        public SkipCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name == Skipped)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason")
                    {
                        _reasons.Enqueue(tag.Value as string);
                    }
                }
            });
            _listener.Start();
        }

        public int Count(string reason) => _reasons.Count(r => r == reason);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The shared pool, except that <see cref="Rent"/> fails while <see cref="Fail"/> is set (a buffer that cannot be had).</summary>
    private sealed class FailingPool : ArrayPool<byte>
    {
        public bool Fail { get; set; }

        public override byte[] Rent(int minimumLength) =>
            Fail ? throw new InsufficientMemoryException("no buffer") : Shared.Rent(minimumLength);

        public override void Return(byte[] array, bool clearArray = false) => Shared.Return(array, clearArray);
    }
}
