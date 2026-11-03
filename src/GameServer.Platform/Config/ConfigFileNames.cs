using System.Globalization;
using System.Text;

namespace GameServer.Platform.Config;

/// <summary>
/// The control-character rule for config file names (slice 015 T071 m3): the loader refuses such a name and
/// <see cref="GameConfigVersions.Compute"/> throws, so every name ends at its line feed in the version input.
/// </summary>
internal static class ConfigFileNames
{
    public static bool HasControlCharacter(string name)
    {
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="name"/> with each control character written as <c>\uXXXX</c> (uppercase hex): one report line per fault.</summary>
    public static string Escape(string name)
    {
        if (!HasControlCharacter(name))
        {
            return name;
        }

        var text = new StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
