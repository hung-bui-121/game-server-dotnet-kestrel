using System.Text;
using GameServer.Config;
using GameServer.Platform.Config;

namespace GameServer.TestKit;

/// <summary>
/// Real <see cref="GameConfig"/> instances for tests (FR-021): both versions are computed exactly like the host loader, so a
/// handler or a config check sees the <see cref="IGameConfig.ConfigVersion"/> and <see cref="IGameConfig.ClientConfigVersion"/> a
/// host loading the same bytes would report.
/// </summary>
public static class TestGameConfig
{
    /// <summary>Label "test", no files.</summary>
    public static IGameConfig Empty { get; } = FromJson(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Files given as JSON text (UTF-8); both versions computed exactly like the host loader.</summary>
    public static IGameConfig FromJson(
        IReadOnlyDictionary<string, string> files,
        string label = "test",
        IEnumerable<string>? clientVisible = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, json) in files)
        {
            bytes[name] = Encoding.UTF8.GetBytes(json);
        }

        var visible = new HashSet<string>(clientVisible ?? [], StringComparer.Ordinal);
        foreach (var name in visible)
        {
            if (!bytes.ContainsKey(name))
            {
                throw new ArgumentException($"Client-visible file '{name}' is not one of the given files.", nameof(clientVisible));
            }
        }

        return new GameConfig(label, bytes, visible);
    }

    /// <summary>The host loader itself: <c>{configRoot}/base</c> overlaid by <c>{configRoot}/env/{deploymentEnvironment}</c>.</summary>
    public static IGameConfig FromDirectory(string configRoot, string deploymentEnvironment = "Development") =>
        FileGameConfigLoader.Load(configRoot, deploymentEnvironment);
}
