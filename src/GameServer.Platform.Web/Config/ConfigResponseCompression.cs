using System.Buffers;
using System.IO.Compression;
using GameServer.Platform.Web.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace GameServer.Platform.Web.Config;

/// <summary>
/// Writes <c>GetConfig</c> responses, compressed per request for clients that ask (research R12, contracts/wire-changes.md §5,
/// data-model §4.2). Only the <c>GetConfig</c> handler calls it: no middleware compresses, so no other route (login and session
/// responses carry secrets, BREACH) can ever carry a <c>Content-Encoding</c>.
/// </summary>
internal static class ConfigResponseCompression
{
    /// <summary>Smaller responses (<c>not_modified</c> included) are never compressed (FR-051).</summary>
    public const int MinCompressBytes = 1024;

    /// <summary>
    /// zlib level of gzip responses (PCR platform §1). Measured on this path at 1 MiB (T050): level 9 p95 ≈ 18.2–18.7 ms, within
    /// 10 % of the 20 ms budget (SC-005), so the lower level 6 is used: p95 ≈ 9 ms, ratio 14.0 % instead of 12.9 %.
    /// </summary>
    public const int GzipLevel = 6;

    /// <summary>Brotli quality of br responses (PCR platform §1): quality 6 p95 ≈ 17.9 ms at 1 MiB, quality 4 ≈ 7.5 ms (T050).</summary>
    public const int BrQuality = 4;

    public const string Gzip = "gzip";
    public const string Brotli = "br";

    private const string Wildcard = "*";

    /// <summary>
    /// The full response (<c>Prefix ++ tail</c>): compressed when the switch is on, the response is at least
    /// <see cref="MinCompressBytes"/> long, carries files, the request accepts br or gzip and the result is smaller.
    /// </summary>
    public static async Task WriteFullAsync(HttpContext context, bool enabled, ConfigBundle bundle, ReadOnlyMemory<byte> tail)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!enabled)
        {
            // Rollback switch: today's bytes and headers.
            await ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, bundle.Prefix, tail, contentEncoding: null, contentLength: null);
            return;
        }

        AddVary(context);
        var length = bundle.Prefix.Length + tail.Length;
        var encoding = bundle.FileCount > 0 && length >= MinCompressBytes ? Negotiate(context.Request) : null;
        if (encoding is null)
        {
            await WriteIdentityAsync(context, bundle.Prefix, tail);
            return;
        }

        // Only a strictly smaller result is sent, so the buffer never needs more than the input length.
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = Compress(encoding, bundle.Prefix.Span, tail.Span, buffer, capacity: length - 1);
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
    public static Task WriteNotModifiedAsync(HttpContext context, bool enabled, ReadOnlyMemory<byte> body)
    {
        if (!enabled)
        {
            return ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, body, ReadOnlyMemory<byte>.Empty, contentEncoding: null, contentLength: null);
        }

        AddVary(context);
        return WriteIdentityAsync(context, body, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// <c>br</c>, <c>gzip</c> or null (identity) for the request's <c>Accept-Encoding</c> (wire-changes §5): <c>q=0</c> excludes,
    /// <c>*</c> covers a candidate that is not listed, the higher q wins and a tie goes to <c>br</c>. An absent or unparseable header
    /// names no candidate (the ASP.NET parser never throws and re-tokenises malformed elements, VERIFY V7).
    /// </summary>
    internal static string? Negotiate(HttpRequest request)
    {
        double? brotli = null;
        double? gzip = null;
        double? any = null;
        foreach (var value in request.GetTypedHeaders().AcceptEncoding)
        {
            var quality = value.Quality ?? 1;
            if (value.Value.Equals(Brotli, StringComparison.OrdinalIgnoreCase))
            {
                brotli = Math.Max(brotli ?? 0, quality);
            }
            else if (value.Value.Equals(Gzip, StringComparison.OrdinalIgnoreCase))
            {
                gzip = Math.Max(gzip ?? 0, quality);
            }
            else if (value.Value.Equals(Wildcard, StringComparison.Ordinal))
            {
                any = Math.Max(any ?? 0, quality);
            }
        }

        var brotliQuality = brotli ?? any ?? 0;
        var gzipQuality = gzip ?? any ?? 0;
        if (brotliQuality > 0 && brotliQuality >= gzipQuality)
        {
            return Brotli;
        }

        return gzipQuality > 0 ? Gzip : null;
    }

    /// <summary>
    /// Compresses <c>prefix ++ tail</c> into <paramref name="destination"/>; returns the length written, or -1 when the result needs
    /// more than <paramref name="capacity"/> bytes.
    /// </summary>
    internal static int Compress(string encoding, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> tail, byte[] destination, int capacity)
    {
        var sink = new BoundedSink(destination, capacity);
        using (var encoder = Encoder(encoding, sink))
        {
            encoder.Write(prefix);
            encoder.Write(tail);
        }

        return sink.Overflowed ? -1 : sink.Written;
    }

    private static Stream Encoder(string encoding, Stream sink) => encoding switch
    {
        Brotli => new BrotliStream(sink, new BrotliCompressionOptions { Quality = BrQuality }, leaveOpen: true),
        Gzip => new GZipStream(sink, new ZLibCompressionOptions { CompressionLevel = GzipLevel }, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Only br and gzip are offered."),
    };

    private static Task WriteIdentityAsync(HttpContext context, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) =>
        ProtobufBinding.WriteBytesAsync(context, StatusCodes.Status200OK, first, second, contentEncoding: null, first.Length + second.Length);

    /// <summary>A shared cache must never give a compressed body to a client that did not ask (R12).</summary>
    private static void AddVary(HttpContext context) => context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

    /// <summary>
    /// Write-only stream over a fixed buffer: past <c>capacity</c> it records the overflow and drops the rest, so a result that is
    /// not smaller costs no growth and no exception.
    /// </summary>
    private sealed class BoundedSink(byte[] target, int capacity) : Stream
    {
        public int Written { get; private set; }

        public bool Overflowed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            if (Overflowed)
            {
                return;
            }

            if (source.Length > capacity - Written)
            {
                Overflowed = true;
                return;
            }

            source.CopyTo(target.AsSpan(Written));
            Written += source.Length;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
