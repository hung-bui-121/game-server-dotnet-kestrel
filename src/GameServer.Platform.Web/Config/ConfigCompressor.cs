using System.Buffers;
using GameServer.Platform.Web.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace GameServer.Platform.Web.Config;

/// <summary>
/// How <c>GetConfig</c> responses of this host are compressed (research R12, slice 015 T071 M3): the rollback switch, the encodings
/// worth offering (probed once at startup: an encoding that does not shrink the bundle is never tried per request), and a
/// process-wide gate. At most as many responses as the gate has permits are compressed at the same time; a response that finds no
/// free permit is sent uncompressed, the body every client handles, so a burst of full downloads cannot take the CPU.
/// </summary>
internal sealed class ConfigCompressor
{
    /// <summary>Processors per concurrent compression: <c>max(1, CPUs / 4)</c> responses may be compressed at once.</summary>
    private const int ProcessorsPerCompression = 4;

    private readonly SemaphoreSlim _gate;

    private ConfigCompressor(bool enabled, bool offersBrotli, bool offersGzip, SemaphoreSlim gate)
    {
        Enabled = enabled;
        OffersBrotli = offersBrotli;
        OffersGzip = offersGzip;
        _gate = gate;
    }

    /// <summary>Concurrent compressions of this process: <c>max(1, Environment.ProcessorCount / 4)</c>.</summary>
    public static int MaxConcurrentCompressions => Math.Max(1, Environment.ProcessorCount / ProcessorsPerCompression);

    /// <summary><c>GameServer:ConfigCompression</c>; false = the rollback switch (no compression, no <c>Vary</c>).</summary>
    public bool Enabled { get; }

    /// <summary>Brotli made the largest response of the bundle smaller at startup.</summary>
    public bool OffersBrotli { get; }

    /// <summary>gzip made the largest response of the bundle smaller at startup.</summary>
    public bool OffersGzip { get; }

    /// <param name="gate">Permits = concurrent compressions; the host passes <c>new SemaphoreSlim(MaxConcurrentCompressions)</c>.</param>
    public static ConfigCompressor Create(ConfigBundle bundle, bool enabled, SemaphoreSlim gate)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.CurrentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gate), gate.CurrentCount, "The compression gate must allow at least one compression.");
        }

        // Nothing is offered (and nothing probed) when no response can be compressed: off, no files, or always under the threshold.
        if (!enabled || bundle.FileCount == 0 || bundle.MaxResponseBytes < ConfigResponseCompression.MinCompressBytes)
        {
            return new ConfigCompressor(enabled, offersBrotli: false, offersGzip: false, gate);
        }

        return new ConfigCompressor(
            enabled, Shrinks(bundle, ConfigResponseCompression.Brotli), Shrinks(bundle, ConfigResponseCompression.Gzip), gate);
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
        var encoding = bundle.FileCount > 0 && length >= ConfigResponseCompression.MinCompressBytes
            ? ConfigResponseCompression.Negotiate(context.Request, OffersBrotli, OffersGzip)
            : null;
        if (encoding is null || !_gate.Wait(0))
        {
            await WriteIdentityAsync(context, bundle.Prefix, tail);
            return;
        }

        // Only a strictly smaller result is sent, so the buffer never needs more than the input length.
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int written;
            try
            {
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
            ArrayPool<byte>.Shared.Return(buffer);
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
    private static bool Shrinks(ConfigBundle bundle, string encoding)
    {
        var tail = bundle.Tail(long.MinValue);
        var buffer = ArrayPool<byte>.Shared.Rent(bundle.MaxResponseBytes);
        try
        {
            return ConfigResponseCompression.Compress(encoding, bundle.Prefix.Span, tail, buffer, capacity: bundle.MaxResponseBytes - 1) >= 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task WriteIdentityAsync(HttpContext context, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) =>
        ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, first, second, contentEncoding: null, first.Length + second.Length);

    /// <summary>A shared cache must never give a compressed body to a client that did not ask (R12).</summary>
    private static void AddVary(HttpContext context) => context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
}
