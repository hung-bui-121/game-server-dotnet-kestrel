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

    // ---- slice 015 T016 (FR-001..FR-003, BC-2, BC-3): every fault in one report ----

    [Fact]
    public void Two_missing_files_and_one_invalid_json_file_give_exactly_three_lines()
    {
        Write("base/manifest.json", """{"label":"v1","files":["battle.json","shop.json","iap.json","ok.json"],"clientVisible":["battle.json","shop.json"]}""");
        Write("base/iap.json", "{\n  \"products\": {\n    \"google:x\" }\n}");
        Write("base/ok.json", "{}");
        string parserMessage;
        try
        {
            System.Text.Json.JsonDocument.Parse("{\n  \"products\": {\n    \"google:x\" }\n}");
            throw new InvalidOperationException("expected a parse error");
        }
        catch (System.Text.Json.JsonException ex)
        {
            parserMessage = ex.Message;
        }

        var violations = Fails();

        violations.ShouldBe(
        [
            $"[config] file '{Base("battle.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            $"[config] file '{Base("shop.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            $"[config] '{Base("iap.json")}' is not valid JSON: {parserMessage}. Fix: correct the file (CFG-01).",
        ]);
    }

    [Fact]
    public void Base_and_env_manifest_faults_are_reported_in_one_start()
    {
        Write("base/manifest.json", """{"label":"","files":["a.json"],"clientVisible":["a.json"]}""");
        Write("env/Staging/manifest.json", "{ not json");

        var violations = Fails("Staging");

        violations.Count.ShouldBe(3);
        violations[0].ShouldBe($"[config] file '{Base("a.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.");
        violations[1].ShouldStartWith($"[config] '{Path.Combine(_root, "env", "Staging", "manifest.json")}' is not valid JSON: ");
        violations[1].ShouldEndWith(". Fix: correct the manifest (CFG-01).");
        violations[2].ShouldBe("[config] manifest 'label' is empty. Fix: set a label in config/base/manifest.json (CFG-01).");
    }

    [Fact]
    public void Every_client_visible_name_missing_from_files_has_its_own_line()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"],"clientVisible":["a.json","b.json","c.json","b.json"]}""");
        Write("base/a.json", "{}");

        Fails().ShouldBe(
        [
            "[config] clientVisible file 'b.json' is not listed in 'files'. Fix: add it to manifest 'files'.",
            "[config] clientVisible file 'c.json' is not listed in 'files'. Fix: add it to manifest 'files'.",
        ]);
    }

    [Fact]
    public void A_repeated_manifest_property_is_one_line_and_skips_its_file_list()
    {
        Write("base/manifest.json", "{\n  \"label\": \"v1\",\n  \"files\": [\"a.json\"],\n  \"files\": [\"missing.json\"]\n}");

        Fails().ShouldBe([Duplicate(Base("manifest.json"), "files", "$", 4, 3)]);
    }

    [Theory]
    [InlineData("{\n  \"a\": 1,\n  \"a\": 2\n}", "a", "$", 3, 3)]
    [InlineData("{\n  \"x\": {\n    \"b\": 1,\n    \"b\": 2\n  }\n}", "b", "$.x", 4, 5)]
    [InlineData("{\n  \"rows\": [\n    {\"id\": 1},\n    {\"id\": 2, \"id\": 3}\n  ]\n}", "id", "$.rows[1]", 4, 15)]
    [InlineData("{\"\\u0061\": 1, \"a\": 2}", "a", "$", 1, 15)]
    [InlineData("{\"products\": {\"google:a\": {}, \"google:a\": {}}}", "google:a", "$.products", 1, 31)]
    [InlineData("{\"a\": [1, [2, {\"k\": 1, \"k\": 2}]]}", "k", "$.a[1][1]", 1, 24)]
    public void A_repeated_property_at_any_depth_names_its_object_line_and_byte(string json, string name, string path, int line, int column)
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"],"clientVisible":[]}""");
        Write("base/a.json", json);

        Fails().ShouldBe([Duplicate(Base("a.json"), name, path, line, column)]);
    }

    [Fact]
    public void Case_variants_are_different_properties()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"],"clientVisible":[]}""");
        Write("base/a.json", """{"a": 1, "A": 2, "n": {"a": 1}}""");

        FileGameConfigLoader.Load(_root, "Development").HasFile("a.json").ShouldBeTrue();
    }

    [Fact]
    public void The_overlay_origin_of_each_file_is_recorded()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json","b.json"],"clientVisible":[]}""");
        Write("base/a.json", "{}");
        Write("base/b.json", "{}");
        Write("env/Staging/manifest.json", """{"label":"","files":["b.json"],"clientVisible":[]}""");
        Write("env/Staging/b.json", """{"b":2}""");

        var staging = FileGameConfigLoader.Load(_root, "Staging");
        var development = FileGameConfigLoader.Load(_root, "Development");

        staging.OverlaidFiles.ShouldBe(["b.json"]);
        staging.OverlayEnvironment.ShouldBe("Staging");
        development.OverlaidFiles.ShouldBeEmpty();
        development.OverlayEnvironment.ShouldBeNull();
    }

    private static string Duplicate(string path, string name, string jsonPath, int line, int column) =>
        $"[config] '{path}' has property '{name}' twice at {jsonPath} (line {line}, byte {column}). Fix: keep one property; the last one used to win silently (CFG-01).";

    private string Base(string file) => Path.Combine(_root, "base", file);

    private IReadOnlyList<string> Fails(string environment = "Development") =>
        Should.Throw<GameServerStartupException>(() => FileGameConfigLoader.Load(_root, environment)).Violations;
}
