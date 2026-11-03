using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T050 (FR-020, FR-025, FR-027, contracts/wiregen-cli.md §2–§4): verbs, options, exit codes and the step order.</summary>
public sealed class CliTests : IDisposable
{
    private readonly TempTree _tree = new();

    public CliTests()
    {
        _tree.Write("game/protos/my_game.proto", """
            syntax = "proto3";
            package my_game.v1;

            import "gameserver/core/v1/core.proto";

            message ProgressState {
              int32 level = 1;
              gameserver.core.v1.Section last = 2;
            }
            """);
        _tree.Write("game/export.json", $$"""
            {
              "format": "gameserver.wire-contract/1",
              "frameworkVersion": "{{Repo.Tool.FrameworkVersion}}",
              "protobufVersion": "{{Repo.Tool.ProtobufVersion}}",
              "routes": [
                { "name": "account.init", "methods": ["POST"], "template": "/api/v1/account/init", "profile": "Player", "surface": "PlayerApi",
                  "owner": "game", "origin": "game", "operation": "account.init" },
                { "name": "identity.login_guest", "methods": ["POST"], "template": "/api/v1/identity/login/guest", "profile": "Public",
                  "surface": "PlayerApi", "owner": "identity", "origin": "framework" }
              ],
              "sections": [
                { "id": "progress", "owner": "game", "origin": "game", "message": "my_game.v1.ProgressState" },
                { "id": "wallet", "owner": "economy", "origin": "framework", "message": "gameserver.economy.v1.WalletState" }
              ]
            }
            """);
        _tree.Write("client/Assets/Marker.txt", "client");
        _tree.Folder("client/Assets/Generated");
        _tree.Write("game/wiregen.json", Repo.Config("""
              "catalog": { "file": "export.json" },
              "protos": [ { "root": "protos", "files": ["my_game.proto"] } ],
              "output": { "root": "../client", "marker": "Assets", "directory": "Assets/Generated" },
              "namespace": "MyGame.Client"
            """));
    }

    public void Dispose() => _tree.Dispose();

    [Theory]
    [InlineData("build", "--config", "x")]
    [InlineData("generate", "--config", "x", "--bogus")]
    [InlineData("generate")]
    [InlineData("generate", "--config")]
    public async Task Unknown_or_incomplete_arguments_exit_1_with_the_usage(params string[] args)
    {
        var (code, _, error) = await RunAsync(args);

        code.ShouldBe(WireGenExitCodes.InvalidInput);
        error.ShouldContain("usage: dotnet run --project <framework>/tools/GameServer.WireGen");
    }

    [Fact]
    public async Task Help_prints_the_usage()
    {
        var (code, output, _) = await RunAsync("--help");

        code.ShouldBe(WireGenExitCodes.Ok);
        output.ShouldContain("verify     write nothing; exit 2 when any output file differs or is missing");
    }

    [Fact]
    public async Task Generate_then_verify_succeeds_and_a_second_generate_writes_nothing()
    {
        var (code, output, error) = await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"));

        code.ShouldBe(WireGenExitCodes.Ok, error);
        output.ShouldContain("wrote Assets/Generated/MyGame.cs");
        output.ShouldContain("new file Assets/Generated/GameRoutes.cs: Unity creates Assets/Generated/GameRoutes.cs.meta on import; commit it");
        output.ShouldContain("generate: 3 written, 0 unchanged");
        var routes = File.ReadAllText(_tree.Path("client/Assets/Generated/GameRoutes.cs"));
        routes.ShouldContain("public const string AccountInit = \"api/v1/account/init\";");
        routes.ShouldNotContain("IdentityLoginGuest", customMessage: "framework routes come from the Unity package (FR-031)");
        var sections = File.ReadAllText(_tree.Path("client/Assets/Generated/GameSections.cs"));
        sections.ShouldContain("SectionKey<global::MyGame.V1.ProgressState> Progress");
        sections.ShouldNotContain("Wallet");

        var before = _tree.Snapshot();
        (await RunAsync("verify", "--config", _tree.Path("game/wiregen.json"))).Code.ShouldBe(WireGenExitCodes.Ok);
        var again = await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"));
        again.Output.ShouldContain("generate: 0 written, 3 unchanged");
        _tree.Snapshot().ShouldBe(before);
    }

    [Fact]
    public async Task Verify_exits_2_naming_an_edited_output()
    {
        (await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"))).Code.ShouldBe(WireGenExitCodes.Ok);
        File.AppendAllText(_tree.Path("client/Assets/Generated/GameRoutes.cs"), "// edited\n");
        File.Delete(_tree.Path("client/Assets/Generated/GameSections.cs"));

        var (code, output, _) = await RunAsync("verify", "--config", _tree.Path("game/wiregen.json"));

        code.ShouldBe(WireGenExitCodes.OutOfDate);
        output.ShouldContain("differs: Assets/Generated/GameRoutes.cs");
        output.ShouldContain("missing: Assets/Generated/GameSections.cs");
        File.Exists(_tree.Path("client/Assets/Generated/GameSections.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_protoc_error_after_a_valid_config_exits_1_and_writes_nothing()
    {
        _tree.Write("game/protos/my_game.proto", "syntax = \"proto3\";\npackage my_game.v1;\nmessage ProgressState { int32 = 1; }\n");
        var before = _tree.Snapshot();

        var (code, _, error) = await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"));

        code.ShouldBe(WireGenExitCodes.InvalidInput);
        error.ShouldContain("[protoc] descriptor pass failed (exit code 1):");
        error.ShouldContain("my_game.proto:3:");
        _tree.Snapshot().ShouldBe(before);
    }

    [Fact]
    public async Task A_version_mismatch_of_the_export_exits_1_before_anything_is_written()
    {
        File.WriteAllText(_tree.Path("game/export.json"), File.ReadAllText(_tree.Path("game/export.json"))
            .Replace($"\"frameworkVersion\": \"{Repo.Tool.FrameworkVersion}\"", "\"frameworkVersion\": \"0.6.0\"", StringComparison.Ordinal));
        var before = _tree.Snapshot();

        var (code, _, error) = await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"));

        code.ShouldBe(WireGenExitCodes.InvalidInput);
        error.ShouldContain($"[version] catalog exported by framework 0.6.0, generator is {Repo.Tool.FrameworkVersion}.");
        _tree.Snapshot().ShouldBe(before);
    }

    [Fact]
    public async Task The_catalog_option_replaces_the_config_catalog()
    {
        File.Move(_tree.Path("game/export.json"), _tree.Path("other-export.json"));

        var (code, _, error) = await RunAsync("generate", "--config", _tree.Path("game/wiregen.json"), "--catalog", _tree.Path("other-export.json"));

        code.ShouldBe(WireGenExitCodes.Ok, error);
    }

    // T086 minor 2: exit codes are a contract; a bug of the tool is exit 1 with one line, not an unhandled exception.
    [Fact]
    public async Task An_unexpected_failure_exits_1_with_an_internal_line_and_writes_nothing()
    {
        var broken = Repo.Tool with { GrpcToolsRoot = null! };
        var before = _tree.Snapshot();
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await WireGenCli.RunAsync(["generate", "--config", _tree.Path("game/wiregen.json")], output, error, broken);

        code.ShouldBe(WireGenExitCodes.InvalidInput);
        error.ToString().ShouldStartWith("[internal] ArgumentNullException: ");
        _tree.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void A_temp_folder_that_cannot_be_deleted_is_a_note_not_an_exception()
    {
        var gone = new DirectoryInfo(_tree.Path("already-deleted"));
        var error = new StringWriter();

        WireGenCli.DeleteTemp(gone, error);

        error.ToString().ShouldStartWith($"note: the temp folder {gone.FullName} could not be deleted (");
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await WireGenCli.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
