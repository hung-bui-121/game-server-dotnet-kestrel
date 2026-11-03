using System.Buffers;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Web.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace GameServer.Platform.Web.Config;

/// <summary>
/// How <c>GetConfig</c> responses of this host are compressed (research R12, slice 015 T071 M3): the rollback switch, the encodings
/// worth offering (probed once at startup: an encoding that does not shrink the bundle is never tried per request), and a gate per
/// host. At most as many responses as the gate has permits are compressed at the same time by this host; a response that finds no
/// free permit is sent uncompressed, the body every client handles, so a burst of full downloads cannot take the CPU. Each such
/// fallback is counted in <c>config_compression_skipped_total{reason}</c> (T071 round 2 m-2).
/// </summary>
internal sealed class ConfigCompressor
{
    /// <summary>Reason of <see cref="PlatformMetrics.ConfigCompressionSkipped"/>: no free permit of this host's gate.</summary>
    public const string SkippedBusy = "busy";

    /// <summary>Reason of <see cref="PlatformMetrics.ConfigCompressionSkipped"/>: the request accepts only encodings the startup probe dropped.</summary>
    public const string SkippedProbe = "probe";

    /// <summary>Processors per concurrent compression: <c>max(1, CPUs / 4)</c> responses may be compressed at once.</summary>
    private const int ProcessorsPerCompression = 4;

    private readonly SemaphoreSlim _gate;
    private readonly PlatformMetrics _metrics;
    private readonly ArrayPool<byte> _pool;

    private ConfigCompressor(bool enabled, bool offersBrotli, bool offersGzip, SemaphoreSlim gate, PlatformMetrics metrics, ArrayPool<byte> pool)
    {
        Enabled = enabled;
        OffersBrotli = offersBrotli;
        OffersGzip = offersGzip;
        Permits = gate.CurrentCount;
        _gate = gate;
        _metrics = metrics;
        _pool = pool;
    }

    /// <summary>Concurrent compressions of one host: <c>max(1, Environment.ProcessorCount / 4)</c>.</summary>
    public static int MaxConcurrentCompressions => Math.Max(1, Environment.ProcessorCount / ProcessorsPerCompression);

    /// <summary><c>GameServer:ConfigCompression</c>; false = the rollback switch (no compression, no <c>Vary</c>).</summary>
    public bool Enabled { get; }

    /// <summary>Brotli made the largest response of the bundle smaller at startup.</summary>
    public bool OffersBrotli { get; }

    /// <summary>gzip made the largest response of the bundle smaller at startup.</summary>
    public bool OffersGzip { get; }

    /// <summary>Permits of this host's gate when it was created: responses this host compresses at the same time.</summary>
    public int Permits { get; }

    /// <param name="gate">Permits = concurrent compressions of this host; the host passes <c>new SemaphoreSlim(MaxConcurrentCompressions)</c>.</param>
    /// <param name="metrics">Counts the responses sent uncompressed by the gate or the probe.</param>
    /// <param name="pool">Buffers of the probe and the responses; null = <see cref="ArrayPool{T}.Shared"/> (tests pass a failing one).</param>
    public static ConfigCompressor Create(ConfigBundle bundle, bool enabled, SemaphoreSlim gate, PlatformMetrics metrics, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(metrics);
        if (gate.CurrentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gate), gate.CurrentCount, "The compression gate must allow at least one compression.");
        }

        pool ??= ArrayPool<byte>.Shared;

        // Nothing is offered (and nothing probed) when no response can be compressed: off, no files, or always under the threshold.
        if (!enabled || bundle.FileCount == 0 || bundle.MaxResponseBytes < ConfigResponseCompression.MinCompressBytes)
        {
            return new ConfigCompressor(enabled, offersBrotli: false, offersGzip: false, gate, metrics, pool);
        }

        return new ConfigCompressor(
            enabled, Shrinks(bundle, ConfigResponseCompression.Brotli, pool), Shrinks(bundle, ConfigResponseCompression.Gzip, pool), gate, metrics, pool);
    }

    /// <summary>
    /// The compression part of the startup config line (data-model §5.6): <c>off</c> (rollback switch), or the encodings the probe
    /// kept (<c>br,gzip</c>, <c>br</c>, <c>gzip</c> or <c>none</c>) and the permits, e.g. <c>br,gzip, permits 2</c>.
    /// </summary>
    public string Describe()
    {
        if (!Enabled)
        {
            return "off";
        }

        var encodings = (OffersBrotli, OffersGzip) switch
        {
            (true, true) => ConfigResponseCompression.Brotli + "," + ConfigResponseCompression.Gzip,
            (true, false) => ConfigResponseCompression.Brotli,
            (false, true) => ConfigResponseCompression.Gzip,
            _ => "none",
        };
        return $"{encodings}, permits {Permits}";
    }

    /// <summary>
    /// The full response (<c>Prefix ++ tail</c>): compressed when the switch is on, the response is at least
    /// <see cref="ConfigResponseCompression.MinCompressBytes"/> long, carries files, the request accepts an offered encoding, a
    /// permit is free and the result is smaller; otherwise the identity bytes.
    /// </summary>
    public async Task WriteFullAsync(HttpContext context, ConfigBundle bundle, ReadOnlyMemory<byte> tail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bundle);
        if (!Enabled)
        {
            // Rollback switch: today's bytes and headers.
            await ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, bundle.Prefix, tail, contentEncoding: null, contentLength: null);
            return;
        }

        AddVary(context);
        var length = bundle.Prefix.Length + tail.Length;
        if (bundle.FileCount == 0 || length < ConfigResponseCompression.MinCompressBytes)
        {
            await WriteIdentityAsync(context, bundle.Prefix, tail);
            return;
        }

        var encoding = ConfigResponseCompression.Negotiate(context.Request, OffersBrotli, OffersGzip);
        if (encoding is null)
        {
            // Parsed a second time only when the probe dropped an encoding: did the client ask for one of those?
            if (!(OffersBrotli && OffersGzip) && ConfigResponseCompression.Negotiate(context.Request, offerBrotli: true, offerGzip: true) is not null)
            {
                _metrics.ConfigCompressionSkipped(SkippedProbe);
            }

            await WriteIdentityAsync(context, bundle.Prefix, tail);
            return;
        }

        if (!_gate.Wait(0))
        {
            _metrics.ConfigCompressionSkipped(SkippedBusy);
            await WriteIdentityAsync(context, bundle.Prefix, tail);
            return;
        }

        byte[]? buffer = null;
        try
        {
            int written;
            try
            {
                // Rented under the permit, inside the try that releases it: a failed rent gives the permit back (T071 round 2 m-4).
                // Only a strictly smaller result is sent, so the buffer never needs more than the input length.
                buffer = _pool.Rent(length);
                written = ConfigResponseCompression.Compress(encoding, bundle.Prefix.Span, tail.Span, buffer, capacity: length - 1);
            }
            finally
            {
                // The permit bounds CPU, not the write: it is released before the body goes out.
                _gate.Release();
            }

            if (written < 0)
            {
                await WriteIdentityAsync(context, bundle.Prefix, tail);
                return;
            }

            await ProtobufBinding.WriteBytesAsync(
                context, StatusCodes.Status200OK, buffer.AsMemory(0, written), ReadOnlyMemory<byte>.Empty, encoding, written);
        }
        finally
        {
            if (buffer is not null)
            {
                _pool.Return(buffer);
            }
        }
    }

    /// <summary>A <c>not_modified</c> response: never compressed, with <c>Vary</c> while the switch is on.</summary>
    public Task WriteNotModifiedAsync(HttpContext context, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enabled)
        {
            return ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, body, ReadOnlyMemory<byte>.Empty, contentEncoding: null, contentLength: null);
        }

        AddVary(context);
        return WriteIdentityAsync(context, body, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>The startup probe: <paramref name="encoding"/> makes the largest response (longest tail) strictly smaller.</summary>
    private static bool Shrinks(ConfigBundle bundle, string encoding, ArrayPool<byte> pool)
    {
        var tail = bundle.Tail(long.MinValue);
        var buffer = pool.Rent(bundle.MaxResponseBytes);
        try
        {
            return ConfigResponseCompression.Compress(encoding, bundle.Prefix.Span, tail, buffer, capacity: bundle.MaxResponseBytes - 1) >= 0;
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static Task WriteIdentityAsync(HttpContext context, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) =>
        ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, first, second, contentEncoding: null, first.Length + second.Length);

    /// <summary>A shared cache must never give a compressed body to a client that did not ask (R12).</summary>
    private static void AddVary(HttpContext context) => context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
}
