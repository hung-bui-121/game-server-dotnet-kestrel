using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Config;

namespace GameServer.Platform.Config;

/// <summary>
/// Loads game config files once at startup (CFG-01): <c>config/base</c> overlaid by
/// <c>config/env/&lt;deployment environment&gt;</c>; an env file replaces the base file with the same name.
/// </summary>
public static class FileGameConfigLoader
{
    public const string ManifestFileName = "manifest.json";

    public static GameConfig Load(string configRoot, string deploymentEnvironment)
    {
        var baseDir = Path.Combine(configRoot, "base");
        var baseManifestPath = Path.Combine(baseDir, ManifestFileName);
        if (!File.Exists(baseManifestPath))
        {
            throw new GameServerStartupException(
                $"[config] '{baseManifestPath}' not found. Fix: create config/base/manifest.json with label, files and clientVisible (CFG-01).");
        }

        var baseManifest = ReadManifest(baseManifestPath);
        var label = baseManifest.Label;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var clientVisible = new HashSet<string>(baseManifest.ClientVisible, StringComparer.Ordinal);

        foreach (var name in baseManifest.Files)
        {
            files[name] = ReadConfigFile(baseDir, name);
        }

        var envDir = Path.Combine(configRoot, "env", deploymentEnvironment);
        var envManifestPath = Path.Combine(envDir, ManifestFileName);
        if (File.Exists(envManifestPath))
        {
            var envManifest = ReadManifest(envManifestPath);
            if (!string.IsNullOrWhiteSpace(envManifest.Label))
            {
                label = envManifest.Label;
            }

            foreach (var name in envManifest.Files)
            {
                files[name] = ReadConfigFile(envDir, name);
            }

            clientVisible.UnionWith(envManifest.ClientVisible);
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new GameServerStartupException("[config] manifest 'label' is empty. Fix: set a label in config/base/manifest.json (CFG-01).");
        }

        foreach (var name in clientVisible.Where(n => !files.ContainsKey(n)))
        {
            throw new GameServerStartupException($"[config] clientVisible file '{name}' is not listed in 'files'. Fix: add it to manifest 'files'.");
        }

        return new GameConfig(label, ComputeVersion(label, files), files, clientVisible);
    }

    /// <summary><c>"{label}+{hex(sha256(concat(sorted(name + '\n' + bytes))))[0..12]}"</c>.</summary>
    public static string ComputeVersion(string label, IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            hash.AppendData(bytes);
        }

        var hex = Convert.ToHexStringLower(hash.GetHashAndReset());
        return $"{label}+{hex[..12]}";
    }

    private static Manifest ReadManifest(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path), GameServerJson.Options);
            return manifest ?? throw new JsonException("empty manifest");
        }
        catch (JsonException ex)
        {
            throw new GameServerStartupException($"[config] '{path}' is not valid JSON: {ex.Message}. Fix: correct the manifest (CFG-01).");
        }
    }

    private static byte[] ReadConfigFile(string dir, string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name))
        {
            throw new GameServerStartupException($"[config] file name '{name}' is not allowed. Fix: use a plain relative file name.");
        }

        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            throw new GameServerStartupException($"[config] file '{path}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.");
        }

        var bytes = File.ReadAllBytes(path);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(bytes);
            }
            catch (JsonException ex)
            {
                throw new GameServerStartupException($"[config] '{path}' is not valid JSON: {ex.Message}. Fix: correct the file (CFG-01).");
            }
        }

        return bytes;
    }

    private sealed class Manifest
    {
        public string Label { get; set; } = string.Empty;

        public string[] Files { get; set; } = [];

        public string[] ClientVisible { get; set; } = [];
    }
}

/// <summary>Immutable effective config (CFG-01). Never cached with a TTL (PERF-08).</summary>
public sealed class GameConfig : IGameConfig
{
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly IReadOnlySet<string> _clientVisible;

    public GameConfig(string label, string configVersion, IReadOnlyDictionary<string, byte[]> files, IReadOnlySet<string> clientVisible)
    {
        Label = label;
        ConfigVersion = configVersion;
        _files = files;
        _clientVisible = clientVisible;
    }

    public string ConfigVersion { get; }

    public string Label { get; }

    public IReadOnlyCollection<string> FileNames => _files.Keys.ToArray();

    public IReadOnlyCollection<string> ClientVisibleFiles => _clientVisible.ToArray();

    public bool HasFile(string name) => _files.ContainsKey(name);

    public ReadOnlyMemory<byte> GetFileBytes(string name) =>
        _files.TryGetValue(name, out var bytes) ? bytes : throw new KeyNotFoundException($"Config file '{name}' not found.");

    public JsonNode GetFileJson(string name) =>
        JsonNode.Parse(GetFileBytes(name).Span) ?? throw new InvalidOperationException($"Config file '{name}' is empty.");

    public T GetFile<T>(string name) =>
        JsonSerializer.Deserialize<T>(GetFileBytes(name).Span, GameServerJson.Options)
        ?? throw new InvalidOperationException($"Config file '{name}' is empty.");
}
