using System.Security.Cryptography;
using System.Text;
using GameServer.Config;
using GameServer.Platform.Config;
using GameServer.Platform.Web.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 015 T071 M3: the startup probe (an encoding that does not shrink the bundle is never offered), negotiation over the offered
/// encodings only, and the process-wide gate (no free permit → the identity body). Host-level rows stay in <see cref="ConfigCompressionTests"/>.
/// </summary>
public sealed class ConfigCompressorTests
{
    private const long ServerTime = 1_792_972_800_000;

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

        var compressor = ConfigCompressor.Create(bundle, enabled: true, new SemaphoreSlim(1));
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

        var both = ConfigCompressor.Create(compressible, enabled: true, new SemaphoreSlim(1));
        var under = ConfigCompressor.Create(small, enabled: true, new SemaphoreSlim(1));
        var off = ConfigCompressor.Create(compressible, enabled: false, new SemaphoreSlim(1));

        (both.OffersBrotli, both.OffersGzip).ShouldBe((true, true));
        (under.OffersBrotli, under.OffersGzip).ShouldBe((false, false));
        (off.Enabled, off.OffersBrotli, off.OffersGzip).ShouldBe((false, false, false));
    }

    [Fact]
    public async Task With_no_free_permit_the_response_is_identity_and_with_one_it_is_compressed()
    {
        var bundle = Bundle(Compressible(4 * 1024));
        var gate = new SemaphoreSlim(1);
        var compressor = ConfigCompressor.Create(bundle, enabled: true, gate);
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

        Should.Throw<ArgumentOutOfRangeException>(() => ConfigCompressor.Create(Bundle(Compressible(4 * 1024)), enabled: true, gate)).ParamName.ShouldBe("gate");
    }

    [Fact]
    public void The_permits_are_a_quarter_of_the_processors_and_at_least_one()
    {
        ConfigCompressor.MaxConcurrentCompressions.ShouldBe(Math.Max(1, Environment.ProcessorCount / 4));
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
}
