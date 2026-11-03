using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameServer.Platform.Config;

/// <summary>The two versions of one effective config (data-model §1.3): <see cref="Server"/> (CFG-01) and <see cref="Client"/> (FG-024).</summary>
public readonly record struct GameConfigVersionPair(string Server, string Client);

/// <summary>
/// The host's version rules (research R2, data-model §1.3); the loader, <see cref="GameConfig"/> and the test kit use them, so a
/// test config and a host loading the same bytes report the same versions. Both are <c>"{label}+{hash12}"</c>.
/// </summary>
/// <remarks>
/// The server version hashes the label, every effective file and the client-visible set; the client version hashes the label
/// and the client-visible files only. The two inputs start with different domain tags, so they never coincide, even when every
/// file is client-visible. Names are length-prefixed by line and contents by byte count, so no two configs share an input.
/// </remarks>
public static class GameConfigVersions
{
    private const string ServerDomain = "gameserver.config.server/1\n";
    private const string ClientDomain = "gameserver.config.client/1\n";

    /// <summary>Lower-case hex digits of the SHA-256 kept in a version (48 bits).</summary>
    private const int HashDigits = 12;

    /// <summary>
    /// Both versions of <paramref name="files"/> (effective files, after the overlay). Order and duplicates of
    /// <paramref name="clientVisible"/> do not matter; every client-visible name must be one of the files.
    /// </summary>
    public static GameConfigVersionPair Compute(string label, IReadOnlyDictionary<string, byte[]> files, IEnumerable<string> clientVisible)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clientVisible);
        var visible = clientVisible.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var name in visible)
        {
            if (!files.ContainsKey(name))
            {
                throw new ArgumentException($"Client-visible file '{name}' is not one of the config files.", nameof(clientVisible));
            }
        }

        using var server = Start(ServerDomain, label);
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
        {
            AppendFile(server, name, files[name]);
        }

        Append(server, "visible\n");
        foreach (var name in visible)
        {
            Append(server, name + "\n");
        }

        using var client = Start(ClientDomain, label);
        foreach (var name in visible)
        {
            AppendFile(client, name, files[name]);
        }

        return new GameConfigVersionPair(Version(label, server), Version(label, client));
    }

    private static IncrementalHash Start(string domain, string label)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, "label\n" + label + "\n");
        return hash;
    }

    private static void AppendFile(IncrementalHash hash, string name, byte[] bytes)
    {
        Append(hash, "file\n" + name + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n");
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

    private static string Version(string label, IncrementalHash hash) =>
        $"{label}+{Convert.ToHexStringLower(hash.GetHashAndReset())[..HashDigits]}";
}
