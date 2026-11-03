#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameServer.Client
{
    /// <summary>
    /// Container format v1 of <see cref="PersistentConfigCache"/> (contracts/sdk-cache.md §2, little-endian): magic "GSCC", format
    /// version, reserved, payload length, payload (version + files sorted by name), SHA-256 of the payload. The format is semantic
    /// for clients: a change is a new format version, and an unknown version reads as empty (FR-064).
    /// </summary>
    internal static class ConfigCacheFormat
    {
        public const ushort FormatVersion = 1;

        public const int HeaderLength = 12;

        public const int HashLength = 32;

        public const int MaxVersionBytes = 256;

        public const int MaxNameBytes = 1024;

        public const int MaxFiles = 100000;

        private static readonly byte[] Magic = { 0x47, 0x53, 0x43, 0x43 };

        // Strict both ways: invalid UTF-8 on read and lone surrogates on write are faults, never silently replaced.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>The container as the three parts written in order (header, payload, hash); null with a reason when not representable.</summary>
        public static byte[][]? Serialize(string version, IReadOnlyDictionary<string, byte[]> files, out string? problem)
        {
            problem = null;
            try
            {
                var versionBytes = Utf8.GetBytes(version);
                if (versionBytes.Length == 0 || versionBytes.Length > MaxVersionBytes || files.Count > MaxFiles)
                {
                    problem = $"not saved: the version must be 1 to {MaxVersionBytes} bytes and at most {MaxFiles} files are kept";
                    return null;
                }

                using var payload = new MemoryStream();
                using (var writer = new BinaryWriter(payload, Utf8, leaveOpen: true))
                {
                    writer.Write((uint)versionBytes.Length);
                    writer.Write(versionBytes);
                    writer.Write((uint)files.Count);
                    foreach (var name in files.Keys.OrderBy(n => n, StringComparer.Ordinal))
                    {
                        var nameBytes = Utf8.GetBytes(name);
                        if (nameBytes.Length == 0 || nameBytes.Length > MaxNameBytes)
                        {
                            problem = $"not saved: a file name must be 1 to {MaxNameBytes} bytes";
                            return null;
                        }

                        writer.Write((uint)nameBytes.Length);
                        writer.Write(nameBytes);
                        writer.Write((uint)files[name].Length);
                        writer.Write(files[name]);
                    }
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
                problem = "not saved: the version or a file name is not valid Unicode";
                return null;
            }
        }

        /// <summary>The stored version and files; null with the reason (sdk-cache §3 kinds) when the container is unusable. Never throws.</summary>
        public static (string Version, Dictionary<string, byte[]> Files)? Parse(byte[] data, out string? problem)
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
                var version = reader.String(MaxVersionBytes);
                var count = reader.UInt32();
                if (version.Length == 0 || count > MaxFiles)
                {
                    problem = "invalid content";
                    return null;
                }

                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                for (var i = 0u; i < count; i++)
                {
                    var name = reader.String(MaxNameBytes);
                    if (name.Length == 0)
                    {
                        problem = "invalid content";
                        return null;
                    }

                    var content = reader.Bytes((int)Math.Min(reader.UInt32(), int.MaxValue));
                    if (files.ContainsKey(name))
                    {
                        problem = "duplicate file name";
                        return null;
                    }

                    files[name] = content;
                }

                if (!reader.AtEnd)
                {
                    problem = "invalid content";
                    return null;
                }

                return (version, files);
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
                return "not a config cache file";
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
