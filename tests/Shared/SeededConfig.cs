using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameServer.Tests.Shared;

/// <summary>
/// A temp copy of the sample test config (<c>{BaseDirectory}/config</c>) that a test may change before starting a host with
/// <c>GameServer:ConfigPath = Path</c> (slice 015 T014). BCL types only: every project that links <c>tests/Shared</c> compiles it.
/// </summary>
public sealed class SeededConfig : IDisposable
{
    public const string ConfigPathKey = "GameServer:ConfigPath";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public SeededConfig()
        : this(System.IO.Path.Combine(AppContext.BaseDirectory, "config"))
    {
    }

    public SeededConfig(string source)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Seeded config: source folder '{source}' does not exist.");
        }

        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gs-seeded-config-" + Guid.NewGuid().ToString("N"));
        Copy(source, Path);
    }

    /// <summary>Config root for <c>GameServer:ConfigPath</c> (holds <c>base</c> and optional <c>env</c>).</summary>
    public string Path { get; }

    public string BasePath => System.IO.Path.Combine(Path, "base");

    /// <summary>Writes (or replaces) <paramref name="file"/> in <c>config/base</c>; the manifest is not changed.</summary>
    public SeededConfig With(string file, string json)
    {
        File.WriteAllText(System.IO.Path.Combine(BasePath, file), json);
        return this;
    }

    /// <summary>Deletes <paramref name="file"/> from <c>config/base</c>; the manifest still lists it.</summary>
    public SeededConfig Without(string file)
    {
        File.Delete(System.IO.Path.Combine(BasePath, file));
        return this;
    }

    /// <summary>Rewrites <c>config/base/manifest.json</c>.</summary>
    public SeededConfig Manifest(Func<JsonObject, JsonObject> edit)
    {
        var path = System.IO.Path.Combine(BasePath, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        File.WriteAllText(path, edit(manifest).ToJsonString(Indented));
        return this;
    }

    /// <summary>Creates <c>config/env/{environment}</c> with <paramref name="files"/> and its manifest.</summary>
    public SeededConfig EnvOverlay(string environment, IReadOnlyDictionary<string, string> files, JsonObject manifest)
    {
        var folder = System.IO.Path.Combine(Path, "env", environment);
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in files)
        {
            File.WriteAllText(System.IO.Path.Combine(folder, name), json);
        }

        File.WriteAllText(System.IO.Path.Combine(folder, "manifest.json"), manifest.ToJsonString(Indented));
        return this;
    }

    /// <summary>Settings callback for <c>SampleTestHost</c>/<c>TestHost</c>: points <c>GameServer:ConfigPath</c> at this copy.</summary>
    public void Apply(Dictionary<string, string?> settings) => settings[ConfigPathKey] = Path;

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            Copy(directory, System.IO.Path.Combine(target, System.IO.Path.GetFileName(directory)));
        }
    }
}
