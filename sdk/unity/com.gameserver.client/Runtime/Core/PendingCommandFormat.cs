#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameServer.Client
{
    /// <summary>
    /// Entry container format v1 of <see cref="PersistentPendingCommandStore"/> (contracts/sdk-pending-store.md §2, little-endian):
    /// magic "GSPC", format version, reserved, payload length, payload (request id, player id, route, recorded-at server time in
    /// milliseconds, body), SHA-256 of the payload. The format is semantic for clients: a change is a new format version, and an
    /// unknown version is skipped (FR-045).
    /// </summary>
    internal static class PendingCommandFormat
    {
        public const ushort FormatVersion = 1;

        public const int HeaderLength = 12;

        public const int HashLength = 32;

        /// <summary>A lowercase UUID in format D.</summary>
        public const int RequestIdLength = 36;

        public const int MaxPlayerIdBytes = 128;

        public const int MaxRouteBytes = 512;

        public const int MaxBodyBytes = 1024 * 1024;

        private static readonly byte[] Magic = { 0x47, 0x53, 0x50, 0x43 };

        // Strict both ways: invalid UTF-8 on read and lone surrogates on write are faults, never silently replaced.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>True for a lowercase UUID in format D (8-4-4-4-12 hex digits): the only request ids that become file names.</summary>
        public static bool IsLowercaseUuid(string? value)
        {
            if (value == null || value.Length != RequestIdLength)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var dash = i == 8 || i == 13 || i == 18 || i == 23;
                if (dash ? c != '-' : !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The container as the three parts written in order (header, payload, hash); null with a reason when not representable.</summary>
        public static byte[][]? Serialize(PendingCommand command, out string? problem)
        {
            problem = null;
            try
            {
                // The stored bytes without a copy: Record runs on the Unity main thread (T082 M3).
                var commandBody = command.BodyBytes;
                var player = Utf8.GetBytes(command.PlayerId);
                var route = Utf8.GetBytes(command.Route);
                if (player.Length == 0 || player.Length > MaxPlayerIdBytes || route.Length == 0 || route.Length > MaxRouteBytes || commandBody.Length > MaxBodyBytes)
                {
                    problem = $"not saved: the player id must be 1 to {MaxPlayerIdBytes} bytes, the route 1 to {MaxRouteBytes} bytes and the body at most {MaxBodyBytes} bytes";
                    return null;
                }

                var id = Utf8.GetBytes(command.RequestId);

                // Four length prefixes and the recorded time: the payload's exact size, so the stream never grows.
                var payloadLength = (4 * sizeof(uint)) + sizeof(long) + id.Length + player.Length + route.Length + commandBody.Length;
                using var payload = new MemoryStream(payloadLength);
                using (var writer = new BinaryWriter(payload, Utf8, leaveOpen: true))
                {
                    writer.Write((uint)id.Length);
                    writer.Write(id);
                    writer.Write((uint)player.Length);
                    writer.Write(player);
                    writer.Write((uint)route.Length);
                    writer.Write(route);
                    writer.Write(command.RecordedAtServerUtc.ToUnixTimeMilliseconds());
                    writer.Write((uint)commandBody.Length);
                    writer.Write(commandBody);
                }

                var body = payload.ToArray();
                var header = new byte[HeaderLength];
                Magic.CopyTo(header, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FormatVersion);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)body.Length);
                return new[] { header, body, Hash(body, 0, body.Length) };
            }
            catch (EncoderFallbackException)
            {
                problem = "not saved: the player id or the route is not valid Unicode";
                return null;
            }
        }

        /// <summary>
        /// The entry stored in <paramref name="data"/>; null with the reason when the container is unusable or its request id is not
        /// <paramref name="expectedRequestId"/> (the file name stem). Never throws.
        /// </summary>
        public static PendingCommand? Parse(byte[] data, string expectedRequestId, out string? problem)
        {
            problem = Check(data);
            if (problem != null)
            {
                return null;
            }

            try
            {
                var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
                var reader = new Reader(data, HeaderLength, HeaderLength + payloadLength);
                var requestId = reader.String(RequestIdLength);
                var playerId = reader.String(MaxPlayerIdBytes);
                var route = reader.String(MaxRouteBytes);
                var recordedAt = reader.Int64();
                var bodyLength = reader.UInt32();
                if (bodyLength > MaxBodyBytes)
                {
                    problem = "invalid content";
                    return null;
                }

                var body = reader.Bytes((int)bodyLength);
                if (!reader.AtEnd)
                {
                    problem = "invalid content";
                    return null;
                }

                if (!IsLowercaseUuid(requestId))
                {
                    problem = "the request id is not a lowercase UUID";
                    return null;
                }

                if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
                {
                    problem = "the request id does not match the file name";
                    return null;
                }

                if (playerId.Length == 0 || route.Length == 0)
                {
                    problem = "invalid content";
                    return null;
                }

                DateTimeOffset recorded;
                try
                {
                    recorded = DateTimeOffset.FromUnixTimeMilliseconds(recordedAt);
                }
                catch (ArgumentOutOfRangeException)
                {
                    problem = "invalid content";
                    return null;
                }

                return new PendingCommand(requestId, playerId, route, body, recorded);
            }
            catch (DecoderFallbackException)
            {
                problem = "invalid UTF-8";
                return null;
            }
            catch (FormatException ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>Header, length and integrity checks, in that order; null when the payload can be read.</summary>
        private static string? Check(byte[] data)
        {
            if (data.Length < HeaderLength)
            {
                return "truncated";
            }

            if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                return "not a pending command file";
            }

            var format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            if (format != FormatVersion)
            {
                return $"unknown format version {format}";
            }

            var expected = (long)HeaderLength + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) + HashLength;
            if (data.Length < expected)
            {
                return "truncated";
            }

            if (data.Length > expected)
            {
                return "trailing bytes";
            }

            var payloadLength = data.Length - HeaderLength - HashLength;
            return Hash(data, HeaderLength, payloadLength).AsSpan().SequenceEqual(data.AsSpan(HeaderLength + payloadLength, HashLength))
                ? null
                : "integrity check failed";
        }

        private static byte[] Hash(byte[] data, int offset, int count)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data, offset, count);
        }

        /// <summary>Reads the payload; every length is checked against the bytes left, so nothing larger than the file is allocated.</summary>
        private sealed class Reader
        {
            private readonly byte[] _data;
            private readonly int _end;
            private int _position;

            public Reader(byte[] data, int start, int end)
            {
                _data = data;
                _position = start;
                _end = end;
            }

            public bool AtEnd => _position == _end;

            public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

            public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

            public string String(int maxBytes)
            {
                var length = UInt32();
                if (length > maxBytes)
                {
                    throw new FormatException("invalid content");
                }

                return Utf8.GetString(Take((int)length));
            }

            public byte[] Bytes(int length) => Take(length).ToArray();

            private ReadOnlySpan<byte> Take(int length)
            {
                if (length < 0 || length > _end - _position)
                {
                    throw new FormatException("truncated");
                }

                var span = _data.AsSpan(_position, length);
                _position += length;
                return span;
            }
        }
    }
}
