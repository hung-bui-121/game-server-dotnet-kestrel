using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T041 (FR-020, FR-024, FR-025, US2-5, data-model §4): the configuration file and its path checks.</summary>
public sealed class WireGenConfigTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_minimal_config_gets_the_documented_defaults()
    {
        var config = Load(Repo.Config("""
              "catalog": { "hostProject": "host" },
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "directory": "client/Generated" },
              "namespace": "Game.Client"
            """));

        config.Environment.ShouldBe("Local");
        config.Origin.ShouldBe("game");
        config.Layout.ShouldBe(OutputLayout.Flat);
        config.LineEndings.ShouldBe(LineEnding.Lf);
        config.RoutesClass.ShouldBe("GameRoutes");
        config.SectionsClass.ShouldBe("GameSections");
        config.OutputRoot.ShouldBe(_tree.Path("tools"));
        config.OutputDirectory.ShouldBe(_tree.Path("tools/client/Generated"));
        config.HostProject.ShouldBe(_tree.Path("tools/host"));
        config.Protos.ShouldHaveSingleItem().Root.ShouldBe(_tree.Path("tools/protos"));
        config.Marker.ShouldBeNull();
    }

    [Fact]
    public void Every_field_is_read_and_paths_are_relative_to_the_config_folder()
    {
        var config = Load(Repo.Config("""
              "catalog": { "file": "export.json", "environment": "Dev" },
              "origin": "framework",
              "protos": [ { "root": "protos", "files": ["game.proto", "sub/extra.proto"] } ],
              "importRoots": ["imports"],
              "output": { "root": "../client", "marker": "Assets", "directory": "Assets/Generated", "layout": "by-root", "lineEndings": "crlf" },
              "namespace": "Game.Client",
              "routesClass": "MyRoutes",
              "sectionsClass": "MySections"
            """));

        config.CatalogFile.ShouldBe(_tree.Path("tools/export.json"));
        config.Environment.ShouldBe("Dev");
        config.Origin.ShouldBe("framework");
        config.ImportRoots.ShouldBe([_tree.Path("tools/imports")]);
        config.OutputRoot.ShouldBe(_tree.Path("client"));
        config.Marker.ShouldBe(_tree.Path("client/Assets"));
        config.OutputDirectory.ShouldBe(_tree.Path("client/Assets/Generated"));
        config.Layout.ShouldBe(OutputLayout.ByRoot);
        config.LineEndings.ShouldBe(LineEnding.Crlf);
        config.Protos.ShouldHaveSingleItem().Files.ShouldBe(["game.proto", "sub/extra.proto"]);
        (config.RoutesClass, config.SectionsClass).ShouldBe(("MyRoutes", "MySections"));
    }

    [Fact]
    public void Mixed_separators_resolve_to_the_same_full_path()
    {
        var slash = Load(Repo.Config("""
              "catalog": { "hostProject": "host" },
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "root": "../client", "marker": "Assets", "directory": "Assets/Generated" },
              "namespace": "Game.Client"
            """));
        var mixed = Load(Repo.Config("""
              "catalog": { "hostProject": "host" },
              "protos": [ { "root": ".\\protos", "files": ["game.proto"] } ],
              "output": { "root": "..\\client/", "marker": "Assets", "directory": "Assets\\Generated" },
              "namespace": "Game.Client"
            """));

        mixed.OutputRoot.ShouldBe(slash.OutputRoot);
        mixed.OutputDirectory.ShouldBe(slash.OutputDirectory);
        mixed.Protos[0].Root.ShouldBe(slash.Protos[0].Root);
    }

    [Theory]
    [InlineData("\"format\": \"gameserver.wiregen/2\",", "format 'gameserver.wiregen/2' is a major version this generator does not read")]
    [InlineData("\"format\": \"something\",", "format 'something' is not a WireGen configuration")]
    [InlineData("", "format is required.")]
    public void A_bad_format_names_the_field(string format, string expected)
    {
        var file = _tree.Write("tools/wiregen.json", "{ " + format + """
             "catalog": { "hostProject": "host" }, "protos": [ { "root": "protos", "files": ["game.proto"] } ],
             "output": { "directory": "client/Generated" }, "namespace": "Game.Client" }
            """);
        Layout();

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("namespace", "namespace is required.")]
    [InlineData("protos", "protos is required")]
    [InlineData("output", "output is required")]
    [InlineData("catalog", "set exactly one of catalog.hostProject")]
    public void A_missing_required_field_is_named(string field, string expected)
    {
        var properties = new Dictionary<string, string>
        {
            ["catalog"] = "\"catalog\": { \"hostProject\": \"host\" }",
            ["protos"] = "\"protos\": [ { \"root\": \"protos\", \"files\": [\"game.proto\"] } ]",
            ["output"] = "\"output\": { \"directory\": \"client/Generated\" }",
            ["namespace"] = "\"namespace\": \"Game.Client\"",
        };
        properties.Remove(field);
        var file = _tree.Write("tools/wiregen.json", Repo.Config(string.Join(",\n", properties.Values)));
        Layout();

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message.ShouldContain(expected);
    }

    [Fact]
    public void The_catalog_option_makes_the_catalog_section_optional_and_wins()
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config("""
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "directory": "client/Generated" },
              "namespace": "Game.Client"
            """));
        Layout();

        WireGenConfig.Load(file, catalogFile: _tree.Path("export.json")).CatalogFile.ShouldBe(_tree.Path("export.json"));
    }

    [Theory]
    [InlineData("\"layout\": \"nested\"", "output.layout 'nested' must be 'flat' or 'by-root'.")]
    [InlineData("\"lineEndings\": \"cr\"", "output.lineEndings 'cr' must be 'lf' or 'crlf'.")]
    public void A_bad_output_option_is_named(string option, string expected)
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config(
            "\"catalog\": { \"hostProject\": \"host\" }, \"protos\": [ { \"root\": \"protos\", \"files\": [\"game.proto\"] } ], " +
            $"\"output\": {{ \"directory\": \"client/Generated\", {option} }}, \"namespace\": \"Game.Client\""));
        Layout();

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("\"namespace\": \"Game.class\"", "namespace 'Game.class' is not a C# namespace")]
    [InlineData("\"namespace\": \"Game\", \"routesClass\": \"1Routes\"", "routesClass '1Routes' is not a C# class name.")]
    [InlineData("\"namespace\": \"Game\", \"routesClass\": \"Same\", \"sectionsClass\": \"Same\"", "routesClass and sectionsClass are both 'Same'.")]
    public void Bad_names_are_refused(string names, string expected)
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config(
            "\"catalog\": { \"hostProject\": \"host\" }, \"protos\": [ { \"root\": \"protos\", \"files\": [\"game.proto\"] } ], " +
            $"\"output\": {{ \"directory\": \"client/Generated\" }}, {names}"));
        Layout();

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("../outside.proto")]
    [InlineData("/abs.proto")]
    [InlineData("game.txt")]
    public void A_proto_path_must_stay_inside_its_root(string proto)
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config(
            $"\"catalog\": {{ \"hostProject\": \"host\" }}, \"protos\": [ {{ \"root\": \"protos\", \"files\": [\"{proto}\"] }} ], " +
            "\"output\": { \"directory\": \"client/Generated\" }, \"namespace\": \"Game\""));
        Layout();

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message.ShouldContain($"protos[0].files '{proto}' must be a .proto path inside its root");
    }

    [Theory]
    [InlineData("root", "output root", "--output-root <client root>")]
    [InlineData("marker", "marker", "--output-root <client root>")]
    [InlineData("directory", "output directory", "never created")]
    [InlineData("proto", "proto game.proto does not exist under its root", "protos[].files")]
    [InlineData("host", "host project", "catalog.hostProject")]
    public void A_missing_path_is_named_and_nothing_is_created(string missing, string expected, string fix)
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config("""
              "catalog": { "hostProject": "../server/Host" },
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "root": "../client", "marker": "Assets", "directory": "Assets/Generated" },
              "namespace": "Game.Client"
            """));
        _tree.Write("tools/protos/game.proto", "syntax = \"proto3\";\n");
        _tree.Folder("client/Assets/Generated");
        _tree.Folder("server/Host");
        switch (missing)
        {
            case "root":
                Directory.Delete(_tree.Path("client"), recursive: true);
                break;
            case "marker":
                Directory.Delete(_tree.Path("client/Assets"), recursive: true);
                _tree.Folder("client");
                break;
            case "directory":
                Directory.Delete(_tree.Path("client/Assets/Generated"));
                break;
            case "proto":
                File.Delete(_tree.Path("tools/protos/game.proto"));
                break;
            default:
                Directory.Delete(_tree.Path("server"), recursive: true);
                break;
        }

        var before = _tree.Snapshot();

        var ex = Should.Throw<WireGenException>(() => WireGenConfig.Load(file));

        ex.Message.ShouldContain(expected);
        ex.Message.ShouldContain(fix);
        _tree.Snapshot().ShouldBe(before);
    }

    // T086 minor 4: the generator writes only inside the output root, so directory and marker may not leave it.
    [Theory]
    [InlineData("directory", "../elsewhere")]
    [InlineData("directory", "Assets/../../elsewhere")]
    [InlineData("directory", "ABSOLUTE")]
    [InlineData("marker", "../elsewhere")]
    [InlineData("marker", "ABSOLUTE")]
    public void Output_directory_and_marker_must_stay_inside_the_output_root(string field, string value)
    {
        var path = value == "ABSOLUTE" ? Repo.Json(_tree.Path("client/Assets/Generated")) : value;
        var directory = field == "directory" ? path : "Assets/Generated";
        var marker = field == "marker" ? path : "Assets";
        var file = _tree.Write("tools/wiregen.json", Repo.Config(
            "\"catalog\": { \"hostProject\": \"host\" }, \"protos\": [ { \"root\": \"protos\", \"files\": [\"game.proto\"] } ], " +
            $"\"output\": {{ \"root\": \"../client\", \"marker\": \"{marker}\", \"directory\": \"{directory}\" }}, \"namespace\": \"Game\""));
        Layout();
        _tree.Folder("client/Assets/Generated");
        _tree.Folder("elsewhere");

        Should.Throw<WireGenException>(() => WireGenConfig.Load(file)).Message
            .ShouldContain($"output.{field} '{path}' is absolute or leaves the output root {_tree.Path("client")} (resolves to ");
    }

    [Fact]
    public void An_output_directory_equal_to_the_root_or_with_an_inner_dot_dot_is_accepted()
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config("""
              "catalog": { "hostProject": "host" },
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "root": "../client", "marker": "Assets/../Assets", "directory": "." },
              "namespace": "Game"
            """));
        Layout();
        _tree.Folder("client/Assets");

        var config = WireGenConfig.Load(file);

        config.OutputDirectory.ShouldBe(_tree.Path("client"));
        config.Marker.ShouldBe(_tree.Path("client/Assets"));
    }

    [Fact]
    public void The_output_root_option_replaces_output_root()
    {
        var file = _tree.Write("tools/wiregen.json", Repo.Config("""
              "catalog": { "hostProject": "host" },
              "protos": [ { "root": "protos", "files": ["game.proto"] } ],
              "output": { "root": "../nowhere", "marker": "Assets", "directory": "Assets/Generated" },
              "namespace": "Game.Client"
            """));
        Layout();
        _tree.Folder("other/Assets/Generated");

        var config = WireGenConfig.Load(file, outputRoot: _tree.Path("other"));

        config.OutputDirectory.ShouldBe(_tree.Path("other/Assets/Generated"));
    }

    private WireGenConfig Load(string json)
    {
        var file = _tree.Write("tools/wiregen.json", json);
        Layout();
        _tree.Folder("client/Assets/Generated");
        _tree.Folder("tools/client/Generated");
        _tree.Folder("tools/imports");
        _tree.Write("tools/protos/sub/extra.proto", "syntax = \"proto3\";\n");
        _tree.Write("tools/export.json", "{}");
        return WireGenConfig.Load(file);
    }

    /// <summary>The folders a valid default config points at.</summary>
    private void Layout()
    {
        _tree.Write("tools/protos/game.proto", "syntax = \"proto3\";\n");
        _tree.Folder("tools/client/Generated");
        _tree.Folder("tools/host");
    }
}
