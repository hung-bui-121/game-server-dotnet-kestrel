using System.Text.RegularExpressions;
using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>
/// Slice 014 T045 (FR-020, FR-021, FR-022, FR-024, FR-025, US2-2, research R11, V6–V8): the pinned protoc, the framework import
/// roots, the descriptor pass, the C# pass and the proto model.
/// </summary>
public sealed partial class ProtocTests : IDisposable
{
    private const string GameProto = """
        syntax = "proto3";
        package fixture.v1;

        import "gameserver/core/v1/core.proto";

        message CardState {
          message Inner { int32 level = 1; }
          map<string, int32> counts = 1;
          repeated Inner inners = 2;
          gameserver.core.v1.Section section = 3;
        }
        """;

    private readonly TempTree _tree = new();
    private readonly Protoc _protoc = Protoc.Locate(Repo.Tool);

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task A_game_proto_importing_core_gets_one_file_without_framework_types()
    {
        var proto = Game("fixture_game.proto", GameProto);
        var roots = _protoc.ImportRoots([proto.Root]);

        var file = await _protoc.GenerateCSharpAsync(roots, proto, OutputLayout.Flat, _tree.Folder("temp"));

        file.RelativePath.ShouldBe("FixtureGame.cs");
        var classes = PartialClass().Matches(file.Text).Select(m => m.Groups[1].Value).ToArray();
        classes.ShouldBe(["FixtureGameReflection", "CardState", "Types", "Inner"], ignoreOrder: true);

        // The imported framework message is referenced from the Unity package, never generated again (FR-021).
        file.Text.ShouldContain("global::GameServer.Protocol.Core.V1.Section");
        classes.ShouldNotContain("Section");

        // Only the relative proto path reaches the header (FR-023).
        file.Text.ShouldContain("//     source: fixture_game.proto");
        file.Text.ShouldNotContain(_tree.Root);
    }

    [Fact]
    public async Task The_model_names_csharp_types_like_protoc()
    {
        var model = await Model(Game("fixture_game.proto", GameProto));

        Single(model, "fixture.v1.CardState").CSharpName.ShouldBe("Fixture.V1.CardState");
        Single(model, "fixture.v1.CardState.Inner").CSharpName.ShouldBe("Fixture.V1.CardState.Types.Inner");
        Single(model, "gameserver.core.v1.Section").CSharpName.ShouldBe("GameServer.Protocol.Core.V1.Section");
        Single(model, "gameserver.economy.v1.WalletState").File.ShouldBe("economy.proto");
        model.Messages.ShouldNotContain(m => m.FullName.EndsWith("CountsEntry", StringComparison.Ordinal), "map entries have no C# type");
        model.IsConsumer("fixture_game.proto").ShouldBeTrue();
        model.TryGetClientMessage("gameserver.economy.v1.WalletState", out _).ShouldBeTrue();
        model.Clashes().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("fixture.v1", "Fixture.V1")]
    [InlineData("my_game.v2beta", "MyGame.V2Beta")]
    [InlineData("samplegame.v1", "Samplegame.V1")]
    [InlineData("a_b.c3d", "AB.C3D")]
    public async Task A_package_without_csharp_namespace_follows_the_protoc_rule(string package, string expected)
    {
        Names.ProtocNamespace(package).ShouldBe(expected);
        var proto = Game("named.proto", $"syntax = \"proto3\";\npackage {package};\nmessage Thing {{ int32 a = 1; }}\n");

        var file = await _protoc.GenerateCSharpAsync(_protoc.ImportRoots([proto.Root]), proto, OutputLayout.Flat, _tree.Folder("temp"));

        file.Text.ShouldContain($"namespace {expected} {{");
    }

    [Fact]
    public async Task A_syntax_error_is_relayed_with_file_and_line()
    {
        var proto = Game("broken.proto", "syntax = \"proto3\";\npackage fixture.v1;\nmessage Broken { int32 = 1; }\n");

        var ex = await Should.ThrowAsync<WireGenException>(() => Model(proto));

        ex.Lines[0].ShouldBe("[protoc] descriptor pass failed (exit code 1):");
        ex.Lines.ShouldContain(l => l.Contains("broken.proto:3:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redefining_a_framework_message_fails_in_the_descriptor_pass()
    {
        var proto = Game("dup.proto", "syntax = \"proto3\";\npackage gameserver.core.v1;\nmessage Section { int32 a = 1; }\n");

        var ex = await Should.ThrowAsync<WireGenException>(() => Model(proto));

        ex.Lines.ShouldContain(l => l.Contains("\"gameserver.core.v1.Section\" is already defined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_package_with_the_framework_csharp_namespace_and_message_name_clashes()
    {
        var proto = Game("clash.proto", """
            syntax = "proto3";
            package other.core;
            option csharp_namespace = "GameServer.Protocol.Core.V1";
            message Section { int32 a = 1; }
            """);

        var clash = (await Model(proto)).Clashes().ShouldHaveSingleItem();

        clash.ShouldBe(
            "[proto] message other.core.Section (clash.proto) and message gameserver.core.v1.Section (gameserver/core/v1/core.proto) both become the C# " +
            "type GameServer.Protocol.Core.V1.Section. Fix: rename the game message or give its file another csharp_namespace (FR-021).");
    }

    [Fact]
    public async Task By_root_puts_core_under_its_folder()
    {
        var core = new ProtoFile(Repo.Path("proto"), "gameserver/core/v1/core.proto");

        var file = await _protoc.GenerateCSharpAsync(_protoc.ImportRoots([core.Root]), core, OutputLayout.ByRoot, _tree.Folder("temp"));

        file.RelativePath.ShouldBe("gameserver/core/v1/Core.cs");
        file.Text.ShouldContain("//     source: gameserver/core/v1/core.proto");
    }

    [Fact]
    public void The_framework_roots_are_always_there()
    {
        _protoc.FrameworkProtos().Select(p => p.Relative).ShouldBe(["economy.proto", "gameserver/core/v1/core.proto", "iap.proto", "identity.proto"]);
        _protoc.ImportRoots([]).ShouldBe(
        [
            Repo.Path("proto"),
            Repo.Path("src/GameServer.Modules.Identity.Adapters/Protos"),
            Repo.Path("src/GameServer.Modules.Economy.Adapters/Protos"),
            Repo.Path("src/GameServer.Modules.Iap.Adapters/Protos"),
            _protoc.IncludeRoot,
        ]);
    }

    private ProtoFile Game(string name, string text)
    {
        _tree.Write("protos/" + name, text);
        return new ProtoFile(_tree.Path("protos"), name);
    }

    private async Task<ProtoModel> Model(ProtoFile proto)
    {
        var framework = _protoc.FrameworkProtos();
        var set = await _protoc.DescribeAsync(_protoc.ImportRoots([proto.Root]), framework.Prepend(proto), _tree.Folder("temp"));
        return ProtoModel.Build(set, [proto.Relative], framework.Select(f => f.Relative));
    }

    private static ProtoMessage Single(ProtoModel model, string fullName) => model.Messages.Single(m => m.FullName == fullName);

    [GeneratedRegex(@"partial class (\w+)")]
    private static partial Regex PartialClass();
}
