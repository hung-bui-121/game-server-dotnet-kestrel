using System.Security.Cryptography;
using System.Text;
using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>
/// FR-040..FR-042, FR-045, SC-004 (slice 015 T034): what changes the server and the client config version (data-model §1.4), with
/// <see cref="GameConfig"/> (what <c>TestGameConfig</c> builds) and with the loader on temp folders (base + env overlay).
/// </summary>
public sealed class GameConfigVersionsTests : IDisposable
{
    private const string Environment = "Staging";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-versions-" + Guid.NewGuid().ToString("N"));

    public GameConfigVersionsTests() => Directory.CreateDirectory(Path.Combine(_root, "base"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_server_only_edit_changes_the_server_version_only()
    {
        var before = Versions(Files(("a.json", "{}"), ("server.json", """{"x":1}""")), "a.json");
        var after = Versions(Files(("a.json", "{}"), ("server.json", """{"x":2}""")), "a.json");

        after.Server.ShouldNotBe(before.Server);
        after.Client.ShouldBe(before.Client);
    }

    [Fact]
    public void A_client_visible_edit_changes_both_versions()
    {
        var before = Versions(Files(("a.json", """{"x":1}"""), ("server.json", "{}")), "a.json");
        var after = Versions(Files(("a.json", """{"x":2}"""), ("server.json", "{}")), "a.json");

        after.Server.ShouldNotBe(before.Server);
        after.Client.ShouldNotBe(before.Client);
    }

    [Fact]
    public void Making_a_file_visible_or_hidden_changes_both_versions()
    {
        var files = Files(("a.json", "{}"), ("b.json", "{}"));
        var one = Versions(files, "a.json");
        var two = Versions(files, "a.json", "b.json");

        two.Server.ShouldNotBe(one.Server);
        two.Client.ShouldNotBe(one.Client);
        Versions(files, "a.json").ShouldBe(one);
    }

    [Fact]
    public void A_label_change_changes_both_versions_and_their_prefix()
    {
        var files = Files(("a.json", "{}"));
        var v1 = GameConfigVersions.Compute("v1", files, ["a.json"]);
        var v2 = GameConfigVersions.Compute("v2", files, ["a.json"]);

        v1.Server.ShouldStartWith("v1+");
        v1.Client.ShouldStartWith("v1+");
        v2.Server[3..].ShouldNotBe(v1.Server[3..]);
        v2.Client[3..].ShouldNotBe(v1.Client[3..]);
    }

    [Fact]
    public void Order_and_duplicates_of_the_visible_list_do_not_matter()
    {
        var files = Files(("a.json", "{}"), ("b.json", "[]"));

        GameConfigVersions.Compute("v", files, ["b.json", "a.json", "b.json"]).ShouldBe(GameConfigVersions.Compute("v", files, ["a.json", "b.json"]));
    }

    [Fact]
    public void Every_file_visible_still_gives_two_different_versions()
    {
        var pair = Versions(Files(("a.json", "{}")), "a.json");

        pair.Server.ShouldNotBe(pair.Client);
        pair.Server.ShouldMatch("^test\\+[0-9a-f]{12}$");
        pair.Client.ShouldMatch("^test\\+[0-9a-f]{12}$");
    }

    [Fact]
    public void Lengths_prefix_the_names_so_moving_bytes_between_name_and_content_changes_the_version()
    {
        var a = Versions(Files(("a", "bc")), "a");
        var b = Versions(Files(("ab", "c")), "ab");

        b.Server.ShouldNotBe(a.Server);
        b.Client.ShouldNotBe(a.Client);
    }

    [Fact]
    public void The_versions_follow_the_documented_formula()
    {
        var files = Files(("b.json", """{"n":2}"""), ("a.json", """{"n":1}"""));
        var server = Encoding.UTF8.GetBytes("gameserver.config.server/1\nlabel\nv7\nfile\na.json\n7\n{\"n\":1}file\nb.json\n7\n{\"n\":2}visible\nb.json\n");
        var client = Encoding.UTF8.GetBytes("gameserver.config.client/1\nlabel\nv7\nfile\nb.json\n7\n{\"n\":2}");

        var pair = GameConfigVersions.Compute("v7", files, ["b.json"]);

        pair.Server.ShouldBe("v7+" + Convert.ToHexStringLower(SHA256.HashData(server))[..12]);
        pair.Client.ShouldBe("v7+" + Convert.ToHexStringLower(SHA256.HashData(client))[..12]);
    }

    [Fact]
    public void A_visible_name_that_is_not_a_file_is_refused()
    {
        Should.Throw<ArgumentException>(() => GameConfigVersions.Compute("v", Files(("a.json", "{}")), ["b.json"])).ParamName.ShouldBe("clientVisible");
    }

    [Theory]
    [InlineData("a\nb.json", "a\\u000Ab.json")]
    [InlineData("a\u0000.json", "a\\u0000.json")]
    [InlineData("tab\t.json", "tab\\u0009.json")]
    public void A_file_name_with_a_control_character_is_refused(string name, string escaped)
    {
        var ex = Should.Throw<ArgumentException>(() => GameConfigVersions.Compute("v", Files(("a.json", "{}"), (name, "{}")), []));

        ex.ParamName.ShouldBe("files");
        ex.Message.ShouldStartWith($"Config file name '{escaped}' contains a control character.");
    }

    [Fact]
    public void The_loader_and_the_constructor_agree_and_two_loads_are_equal()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json","s.json"],"clientVisible":["a.json"]}""");
        Write("base/a.json", """{"x":1}""");
        Write("base/s.json", """{"y":1}""");

        var loaded = FileGameConfigLoader.Load(_root, Environment);
        var built = new GameConfig("v1", Files(("a.json", """{"x":1}"""), ("s.json", """{"y":1}""")), ["a.json"]);

        loaded.ConfigVersion.ShouldBe(built.ConfigVersion);
        loaded.ClientConfigVersion.ShouldBe(built.ClientConfigVersion);
        FileGameConfigLoader.Load(_root, Environment).ClientConfigVersion.ShouldBe(loaded.ClientConfigVersion);
    }

    [Fact]
    public void The_loader_versions_follow_the_matrix_through_base_edits_and_the_overlay()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json","s.json"],"clientVisible":["a.json"]}""");
        Write("base/a.json", """{"x":1}""");
        Write("base/s.json", """{"y":1}""");
        var start = Load();

        Write("base/s.json", """{"y":2}""");
        var serverEdit = Load();
        serverEdit.Server.ShouldNotBe(start.Server);
        serverEdit.Client.ShouldBe(start.Client);

        // Visibility added only through the env overlay: both change.
        Write($"env/{Environment}/manifest.json", """{"label":"","files":[],"clientVisible":["s.json"]}""");
        var overlayVisible = Load();
        overlayVisible.Server.ShouldNotBe(serverEdit.Server);
        overlayVisible.Client.ShouldNotBe(serverEdit.Client);

        // The same bytes overlaid by env give the same versions as in base.
        Write($"env/{Environment}/manifest.json", """{"label":"","files":["s.json"],"clientVisible":[]}""");
        Write($"env/{Environment}/s.json", """{"y":2}""");
        Load().ShouldBe(serverEdit);
    }

    [Fact]
    public void The_obsolete_constructor_keeps_the_given_server_version_and_computes_the_client_version()
    {
        var files = Files(("a.json", "{}"));
#pragma warning disable CS0618 // The obsolete path itself (BC-6).
        var old = new GameConfig("v", "v+000000000000", files, new HashSet<string> { "a.json" });
        var oldVersion = FileGameConfigLoader.ComputeVersion("v", files);
#pragma warning restore CS0618

        old.ConfigVersion.ShouldBe("v+000000000000");
        old.ClientConfigVersion.ShouldBe(new GameConfig("v", files, ["a.json"]).ClientConfigVersion);
        oldVersion.ShouldNotBe(new GameConfig("v", files, ["a.json"]).ConfigVersion);
    }

    private static GameConfigVersionPair Versions(Dictionary<string, byte[]> files, params string[] visible)
    {
        var config = new GameConfig("test", files, visible);
        return new GameConfigVersionPair(config.ConfigVersion, config.ClientConfigVersion);
    }

    private static Dictionary<string, byte[]> Files(params (string Name, string Json)[] files) =>
        files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Json), StringComparer.Ordinal);

    private GameConfigVersionPair Load()
    {
        var config = FileGameConfigLoader.Load(_root, Environment);
        return new GameConfigVersionPair(config.ConfigVersion, config.ClientConfigVersion);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
