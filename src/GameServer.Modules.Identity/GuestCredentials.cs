using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameServer.Errors;

namespace GameServer.Modules.Identity;

/// <summary>Guest credential rules (IDN-02): the local id is only an identifier; the secret proves possession.</summary>
public static partial class GuestCredentials
{
    public const int MinSecretBytes = 32;
    public const int MaxSecretBytes = 256;
    public const string MappingPrefix = "guest:";

    public static bool IsValidLocalId(string localId) => LocalIdPattern().IsMatch(localId);

    public static bool IsValidSecret(ReadOnlySpan<byte> secret) => secret.Length is >= MinSecretBytes and <= MaxSecretBytes;

    public static string MappingKey(string localId) => MappingPrefix + localId;

    /// <summary>Hex HMAC-SHA256 of the secret keyed with the server pepper. The secret itself is never stored.</summary>
    public static string Hash(ReadOnlySpan<byte> pepper, ReadOnlySpan<byte> secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(pepper, secret));

    /// <summary>Constant-time comparison against a stored hash.</summary>
    public static bool Verify(string storedHash, ReadOnlySpan<byte> pepper, ReadOnlySpan<byte> secret)
    {
        Span<byte> expected = stackalloc byte[32];
        if (storedHash.Length != 64 || !TryFromHex(storedHash, expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, HMACSHA256.HashData(pepper, secret));
    }

    private static bool TryFromHex(string hex, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out destination[i]))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex LocalIdPattern();
}

/// <summary>Opaque, stable player ids issued by the server (IDN-01).</summary>
public static class PlayerIds
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz"; // Crockford base32, lowercase

    /// <summary><c>p_</c> + 26 base32 characters of 128 random bits.</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var builder = new StringBuilder("p_", 28);
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}

/// <summary>Account state checks in the order of IDN-10 (session epoch is checked by the caller).</summary>
public static class AccountChecks
{
    public sealed record Denial(ErrorCode Code, IReadOnlyDictionary<string, string>? Details);

    public static Denial? CheckAccess(ProfileState profile, DateTimeOffset now)
    {
        if (profile.Deleted is not null)
        {
            return new Denial(CoreErrorCodes.AccountDeleted, null);
        }

        if (profile.Ban is { } ban && ban.BlocksLogin(now))
        {
            return new Denial(CoreErrorCodes.AccountSuspended, SuspensionDetails(ban));
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> SuspensionDetails(BanInfo ban) =>
        new Dictionary<string, string>
        {
            ["until"] = ban.UntilUtc?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "permanent",
        };
}
