using System.Security.Cryptography;
using System.Text;

namespace GameServer.Platform.Testing;

/// <summary>
/// Document key ↔ file name stem (research R7). Literal bytes are <c>a-z 0-9 . _ -</c>; every other UTF-8 byte is <c>%XX</c>
/// with uppercase hex, so two keys never map to names equal under case folding (Windows, default macOS). Windows device
/// names get their first character encoded; stems longer than <see cref="MaxStemLength"/> become <c>~</c> + base32(SHA-256).
/// </summary>
internal static class FileKeyCodec
{
    /// <summary>Longest encoded stem kept readable; longer ones are hashed (D6, Windows MAX_PATH).</summary>
    public const int MaxStemLength = 120;

    /// <summary>Prefix of hashed names; never a literal, so hashed names never collide with encoded ones.</summary>
    public const char HashPrefix = '~';

    /// <summary>Length of a hashed stem: the prefix plus 52 base32 characters (256 bits).</summary>
    public const int HashedStemLength = 53;

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const string HexDigits = "0123456789ABCDEF";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>File name stem of a key (without <c>.json</c>). The empty key is refused (keys are never empty in the framework).</summary>
    public static string Encode(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var bytes = StrictUtf8.GetBytes(key);
        var stem = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (IsLiteral(b))
            {
                stem.Append((char)b);
            }
            else
            {
                AppendEscaped(stem, b);
            }
        }

        // Only an all-literal (lowercase) prefix can spell a device name: encode its first character (con -> %63on).
        var text = stem.ToString();
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (DeviceNames.Contains(dot < 0 ? text : text[..dot]))
        {
            text = $"%{HexDigits[text[0] >> 4]}{HexDigits[text[0] & 0xF]}{text[1..]}";
        }

        return text.Length <= MaxStemLength ? text : Hashed(bytes);
    }

    /// <summary>
    /// Inverse of <see cref="Encode"/> for readable stems. Refuses hashed stems, lowercase hex, encoded literals and any other
    /// non-canonical or foreign name, so a hand-renamed file fails the load instead of shadowing another key.
    /// </summary>
    public static bool TryDecode(string stem, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(stem) || stem.Length > MaxStemLength || stem[0] == HashPrefix)
        {
            return false;
        }

        var bytes = new List<byte>(stem.Length);
        for (var i = 0; i < stem.Length; i++)
        {
            var c = stem[i];
            if (c == '%')
            {
                if (i + 2 >= stem.Length || HexDigits.IndexOf(stem[i + 1], StringComparison.Ordinal) < 0 || HexDigits.IndexOf(stem[i + 2], StringComparison.Ordinal) < 0)
                {
                    return false;
                }

                bytes.Add((byte)((HexDigits.IndexOf(stem[i + 1], StringComparison.Ordinal) << 4) | HexDigits.IndexOf(stem[i + 2], StringComparison.Ordinal)));
                i += 2;
            }
            else if (c < 0x80 && IsLiteral((byte)c))
            {
                bytes.Add((byte)c);
            }
            else
            {
                return false;
            }
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Canonical only: re-encoding must give the same stem (rejects %61 for 'a', but keeps %63on for the device name con).
        if (decoded.Length == 0 || !string.Equals(Encode(decoded), stem, StringComparison.Ordinal))
        {
            return false;
        }

        key = decoded;
        return true;
    }

    /// <summary>True when <paramref name="stem"/> is the file name of <paramref name="key"/> (readable or hashed).</summary>
    public static bool Matches(string stem, string key)
    {
        try
        {
            return key.Length > 0 && string.Equals(Encode(key), stem, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // A key that is not valid UTF-16 (lone surrogate) has no file name.
            return false;
        }
    }

    private static bool IsLiteral(byte b) => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'.' or (byte)'_' or (byte)'-';

    private static void AppendEscaped(StringBuilder builder, byte b) => builder.Append('%').Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);

    private static string Hashed(byte[] keyBytes)
    {
        var hash = SHA256.HashData(keyBytes);
        var result = new StringBuilder(HashedStemLength).Append(HashPrefix);
        var buffer = 0;
        var bits = 0;
        foreach (var b in hash)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }
}
