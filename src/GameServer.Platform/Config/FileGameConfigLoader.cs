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

    /// <summary>Every fault of the config in one <see cref="GameServerStartupException"/> (BC-3); line texts as before.</summary>
    public static GameConfig Load(string configRoot, string deploymentEnvironment)
    {
        var baseDir = Path.Combine(configRoot, "base");
        var baseManifestPath = Path.Combine(baseDir, ManifestFileName);
        if (!File.Exists(baseManifestPath))
        {
            throw new GameServerStartupException(
                $"[config] '{baseManifestPath}' not found. Fix: create config/base/manifest.json with label, files and clientVisible (CFG-01).");
        }

        var violations = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var clientVisible = new List<string>();
        var overlaid = new HashSet<string>(StringComparer.Ordinal);

        // A manifest fault skips only that manifest's file list; the other checks still run (R3).
        var baseManifest = ReadManifest(baseManifestPath, violations);
        var label = baseManifest?.Label ?? string.Empty;
        if (baseManifest is not null)
        {
            ReadFiles(baseDir, baseManifest, files, listed, clientVisible, violations);
        }

        var envDir = Path.Combine(configRoot, "env", deploymentEnvironment);
        var envManifestPath = Path.Combine(envDir, ManifestFileName);
        var envParsed = true;
        if (File.Exists(envManifestPath))
        {
            var envManifest = ReadManifest(envManifestPath, violations);
            envParsed = envManifest is not null;
            if (envManifest is not null)
            {
                if (!string.IsNullOrWhiteSpace(envManifest.Label))
                {
                    label = envManifest.Label;
                }

                overlaid.UnionWith(ReadFiles(envDir, envManifest, files, listed, clientVisible, violations));
            }
        }

        if (baseManifest is not null && string.IsNullOrWhiteSpace(label))
        {
            violations.Add("[config] manifest 'label' is empty. Fix: set a label in config/base/manifest.json (CFG-01).");
        }

        if (baseManifest is not null && envParsed)
        {
            // Compared with the listed names: a listed file that failed to read already has its own line.
            foreach (var name in clientVisible.Distinct(StringComparer.Ordinal).Where(n => !listed.Contains(n)))
            {
                violations.Add($"[config] clientVisible file '{name}' is not listed in 'files'. Fix: add it to manifest 'files'.");
            }
        }

        if (violations.Count > 0)
        {
            throw new GameServerStartupException(violations);
        }

        return new GameConfig(label, ComputeVersion(label, files), files, clientVisible.ToHashSet(StringComparer.Ordinal))
        {
            OverlaidFiles = overlaid,
            OverlayEnvironment = overlaid.Count > 0 ? deploymentEnvironment : null,
        };
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

    /// <summary>The manifest bound from its parsed document; null (with its lines) when it is not valid JSON or repeats a property.</summary>
    private static Manifest? ReadManifest(string path, List<string> violations)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (AddDuplicates(path, bytes, violations))
            {
                return null;
            }

            return document.Deserialize<Manifest>(GameServerJson.Options) ?? throw new JsonException("empty manifest");
        }
        catch (JsonException ex)
        {
            violations.Add($"[config] '{path}' is not valid JSON: {ex.Message}. Fix: correct the manifest (CFG-01).");
            return null;
        }
    }

    /// <summary>Reads every file of <paramref name="manifest"/> (one line per fault) and returns the names it listed.</summary>
    private static string[] ReadFiles(
        string dir, Manifest manifest, Dictionary<string, byte[]> files, HashSet<string> listed, List<string> clientVisible, List<string> violations)
    {
        clientVisible.AddRange(manifest.ClientVisible ?? []);
        var names = manifest.Files ?? [];
        foreach (var name in names)
        {
            listed.Add(name);
            if (ReadConfigFile(dir, name, violations) is { } bytes)
            {
                files[name] = bytes;
            }
        }

        return names;
    }

    private static byte[]? ReadConfigFile(string dir, string name, List<string> violations)
    {
        if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name))
        {
            violations.Add($"[config] file name '{name}' is not allowed. Fix: use a plain relative file name.");
            return null;
        }

        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            violations.Add($"[config] file '{path}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.");
            return null;
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
                violations.Add($"[config] '{path}' is not valid JSON: {ex.Message}. Fix: correct the file (CFG-01).");
                return null;
            }

            if (AddDuplicates(path, bytes, violations))
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>One line per repeated property name (BC-2); true when there was any.</summary>
    private static bool AddDuplicates(string path, byte[] json, List<string> violations)
    {
        var duplicates = JsonDuplicateProperties.Find(json);
        foreach (var duplicate in duplicates)
        {
            violations.Add(
                $"[config] '{path}' has property '{duplicate.Name}' twice at {duplicate.Path} (line {duplicate.Line}, byte {duplicate.Byte}). " +
                "Fix: keep one property; the last one used to win silently (CFG-01).");
        }

        return duplicates.Count > 0;
    }

    private sealed class Manifest
    {
        public string Label { get; set; } = string.Empty;

        public string[]? Files { get; set; } = [];

        public string[]? ClientVisible { get; set; } = [];
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

    /// <summary>Names whose effective bytes came from <c>config/env/&lt;environment&gt;</c> (report lines name the overlay).</summary>
    internal IReadOnlySet<string> OverlaidFiles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The deployment environment of the overlay folder that supplied <see cref="OverlaidFiles"/>.</summary>
    internal string? OverlayEnvironment { get; init; }

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
