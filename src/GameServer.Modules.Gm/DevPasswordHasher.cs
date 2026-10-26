using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameServer.Modules.Gm;

/// <summary>
/// PBKDF2-SHA256 for Development local GM accounts (research R4). Format: <c>pbkdf2-sha256${iterations}${salt}${hash}</c>
/// (base64). Production staff authenticate with SSO (P0.5) and have no password.
/// </summary>
public static class DevPasswordHasher
{
    public const int Iterations = 100_000;
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string? encoded)
    {
        var parts = encoded?.Split('$');
        if (parts is not [Scheme, var iterationsText, var saltText, var hashText]
            || !int.TryParse(iterationsText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 10_000 or > 10_000_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(saltText), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
