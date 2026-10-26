using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>CFG-01: file config, overlay, deterministic configVersion, fail-fast.</summary>
public sealed class FileGameConfigLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-config-" + Guid.NewGuid().ToString("N"));

    public FileGameConfigLoaderTests() => Directory.CreateDirectory(Path.Combine(_root, "base"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Loads_base_files_and_computes_version()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json","b.json"],"clientVisible":["a.json"]}""");
        Write("base/a.json", """{"x":1}""");
        Write("base/b.json", """{"y":2}""");

        var config = FileGameConfigLoader.Load(_root, "Development");

        config.Label.ShouldBe("v1");
        config.ConfigVersion.ShouldMatch("^v1\\+[0-9a-f]{12}$");
        config.ClientVisibleFiles.ShouldBe(["a.json"]);
        config.GetFileJson("a.json")["x"]!.GetValue<int>().ShouldBe(1);
        FileGameConfigLoader.Load(_root, "Development").ConfigVersion.ShouldBe(config.ConfigVersion);
    }

    [Fact]
    public void Env_overlay_replaces_files_and_changes_version()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"],"clientVisible":[]}""");
        Write("base/a.json", """{"x":1}""");
        var baseVersion = FileGameConfigLoader.Load(_root, "Staging").ConfigVersion;

        Write("env/Staging/manifest.json", """{"label":"","files":["a.json"],"clientVisible":["a.json"]}""");
        Write("env/Staging/a.json", """{"x":2}""");
        var staging = FileGameConfigLoader.Load(_root, "Staging");

        staging.GetFileJson("a.json")["x"]!.GetValue<int>().ShouldBe(2);
        staging.ConfigVersion.ShouldNotBe(baseVersion);
        staging.ConfigVersion.ShouldStartWith("v1+");
        FileGameConfigLoader.Load(_root, "Production").ConfigVersion.ShouldBe(baseVersion);
    }

    [Fact]
    public void Missing_manifest_fails_startup()
    {
        Directory.Delete(Path.Combine(_root, "base"), true);

        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, "Development"))
            .Message.ShouldContain("manifest.json");
    }

    [Fact]
    public void Invalid_json_file_fails_startup()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"]}""");
        Write("base/a.json", "{ not json");

        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, "Development"))
            .Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void Listed_file_must_exist_and_client_visible_must_be_listed()
    {
        Write("base/manifest.json", """{"label":"v1","files":["missing.json"]}""");
        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, "Development"));

        Write("base/manifest.json", """{"label":"v1","files":[],"clientVisible":["a.json"]}""");
        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, "Development"))
            .Message.ShouldContain("clientVisible");
    }

    [Fact]
    public void Path_traversal_is_rejected()
    {
        Write("base/manifest.json", """{"label":"v1","files":["../secret.json"]}""");

        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, "Development"));
    }
}
