using System.Security.Cryptography;

namespace GameServer.Platform.Privacy;

/// <summary>Replacement ids for purged players (PRIV-01, FR-018, data-model.md "Anonymous id").</summary>
public static class AnonymousIds
{
    public const string Prefix = "anon_";

    /// <summary>Random bytes per id: 128 bits, encoded as 26 base32 characters.</summary>
    private const int RandomBytes = 16;

    private const int BitsPerCharacter = 5;
    private const int CharacterMask = 31;
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz"; // Crockford base32, lowercase (same as player ids)

    /// <summary><c>anon_</c> + 26 base32 characters of 128 random bits; never derived from the player id (FR-018).</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        Span<char> text = stackalloc char[Prefix.Length + (((RandomBytes * 8) + BitsPerCharacter - 1) / BitsPerCharacter)];
        Prefix.AsSpan().CopyTo(text);
        var position = Prefix.Length;
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= BitsPerCharacter)
            {
                text[position++] = Alphabet[(buffer >> (bits - BitsPerCharacter)) & CharacterMask];
                bits -= BitsPerCharacter;
            }
        }

        if (bits > 0)
        {
            text[position++] = Alphabet[(buffer << (BitsPerCharacter - bits)) & CharacterMask];
        }

        return new string(text[..position]);
    }
}
