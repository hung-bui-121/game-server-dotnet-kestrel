using GameServer.Config;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T020 (FR-021, research R10): <see cref="TestGameConfig"/> builds the config a host would load — same
/// <see cref="IGameConfig.ConfigVersion"/>, client-visible set and overlay.
/// </summary>
public sealed class TestGameConfigTests : IDisposable
{
    private const string Rules = "{\"maxLevel\":30}";
    private const string Shop = "{\"items\":[\"sword\"]}";

    private readonly string _root = TestStores.NewFolder();

    public void Dispose() => TestStores.DeleteFolder(_root);

    [Fact]
    public async Task From_json_reports_the_version_of_a_host_loading_the_same_bytes_from_disk()
    {
        WriteFolder("base", "v1", new() { ["rules.json"] = Rules, ["shop.json"] = Shop }, ["shop.json"]);

        await using var host = await TestHost.StartAsync(settings: s => s["GameServer:ConfigPath"] = _root);
        var loaded = host.Services.GetRequiredService<IGameConfig>();
        var built = TestGameConfig.FromJson(new Dictionary<string, string> { ["shop.json"] = Shop, ["rules.json"] = Rules }, "v1", ["shop.json"]);

        built.ConfigVersion.ShouldBe(loaded.ConfigVersion);
        built.Label.ShouldBe("v1");
        built.FileNames.ShouldBe(loaded.FileNames, ignoreOrder: true);
        built.ClientVisibleFiles.ShouldBe(["shop.json"]);
        built.GetFile<Dictionary<string, int>>("rules.json")["maxLevel"].ShouldBe(30);
    }

    [Fact]
    public void Client_visible_files_are_honoured_and_must_be_given_files()
    {
        var files = new Dictionary<string, string> { ["rules.json"] = Rules, ["shop.json"] = Shop };

        TestGameConfig.FromJson(files).ClientVisibleFiles.ShouldBeEmpty();
        TestGameConfig.FromJson(files, clientVisible: ["rules.json"]).ClientVisibleFiles.ShouldBe(["rules.json"]);
        Should.Throw<ArgumentException>(() => TestGameConfig.FromJson(files, clientVisible: ["missing.json"]))
            .Message.ShouldContain("'missing.json'");
    }

    [Fact]
    public void Empty_has_the_test_label_and_no_files()
    {
        TestGameConfig.Empty.Label.ShouldBe("test");
        TestGameConfig.Empty.FileNames.ShouldBeEmpty();
        TestGameConfig.Empty.ConfigVersion.ShouldBe(TestGameConfig.FromJson(new Dictionary<string, string>()).ConfigVersion);
    }

    [Fact]
    public async Task From_directory_equals_the_hosts_view_of_the_sample_config()
    {
        await using var host = await SampleTestHost.StartAsync();
        var loaded = host.Services.GetRequiredService<IGameConfig>();

        var built = TestGameConfig.FromDirectory(Path.Combine(AppContext.BaseDirectory, "config"));

        built.ConfigVersion.ShouldBe(loaded.ConfigVersion);
        built.ClientVisibleFiles.ShouldBe(loaded.ClientVisibleFiles, ignoreOrder: true);
    }

    [Fact]
    public async Task From_directory_overlays_the_environment_folder_like_the_host()
    {
        WriteFolder("base", "v1", new() { ["rules.json"] = Rules, ["shop.json"] = Shop }, ["shop.json"]);
        WriteFolder(Path.Combine("env", "Development"), "v1-dev", new() { ["rules.json"] = "{\"maxLevel\":99}" }, ["rules.json"]);

        await using var host = await TestHost.StartAsync(settings: s => s["GameServer:ConfigPath"] = _root);
        var loaded = host.Services.GetRequiredService<IGameConfig>();
        var built = TestGameConfig.FromDirectory(_root, "Development");

        built.ConfigVersion.ShouldBe(loaded.ConfigVersion);
        built.Label.ShouldBe("v1-dev");
        built.GetFile<Dictionary<string, int>>("rules.json")["maxLevel"].ShouldBe(99);
        built.ClientVisibleFiles.ShouldBe(["shop.json", "rules.json"], ignoreOrder: true);
        TestGameConfig.FromDirectory(_root, "Staging").Label.ShouldBe("v1");
    }

    private void WriteFolder(string relative, string label, Dictionary<string, string> files, string[] clientVisible)
    {
        var folder = Path.Combine(_root, relative);
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in files)
        {
            File.WriteAllText(Path.Combine(folder, name), json);
        }

        var manifest = new { label, files = files.Keys.ToArray(), clientVisible };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
    }
}
