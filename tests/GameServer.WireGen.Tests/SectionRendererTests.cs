using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T048 (FR-052, FR-051 generator part, N12, US3-5, contracts/generated-files.md §4): the sections file.</summary>
public sealed class SectionRendererTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task The_framework_sections_render_to_the_golden_file()
    {
        var warnings = new List<string>();
        var sections = new[]
        {
            new ContractSection("wallet", "economy", "framework", "gameserver.economy.v1.WalletState"),
            new ContractSection("profile", "identity", "framework", "gameserver.identity.v1.ProfileState"),
            new ContractSection("inventory", "economy", "framework", "gameserver.economy.v1.InventoryState"),
        };

        var text = SectionRenderer.Render(sections, await FrameworkModelAsync(), "GameServer.Client", "FrameworkSections", "framework", "0.7.0", warnings);

        text.ShouldBe(File.ReadAllText(Repo.TestData("FrameworkSections.golden.txt")).ReplaceLineEndings("\n"));
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_untyped_section_gets_its_id_only_and_a_warning()
    {
        var warnings = new List<string>();

        var text = SectionRenderer.Render(
            [new ContractSection("notes", "fixture", "game", null)], await FrameworkModelAsync(), "Fixture.Client", "FixtureSections", "game", "0.7.0", warnings);

        text.ShouldContain(
            "        /// <summary>Section <c>notes</c> (module <c>fixture</c>) (registered without its message type: no parser).</summary>\n" +
            "        public const string NotesId = \"notes\";\n");
        text.ShouldNotContain(" Notes =");
        text.ShouldContain("public static readonly global::GameServer.Client.SectionTable Table = new global::GameServer.Client.SectionTable();");
        warnings.ShouldHaveSingleItem().ShouldBe(
            "warning: [section notes] is registered without its message type: only FixtureSections.NotesId is generated. Fix: " +
            "AddAggregateWireMapper<TState, TWire>(…) on the server (FG-028).");
    }

    [Fact]
    public async Task A_game_section_reusing_a_framework_message_references_the_framework_type()
    {
        var text = SectionRenderer.Render(
            [new ContractSection("bonus_wallet", "game", "game", "gameserver.economy.v1.WalletState")],
            await FrameworkModelAsync(),
            "Game.Client",
            "GameSections",
            "game",
            "0.7.0",
            []);

        text.ShouldContain(
            "public static readonly global::GameServer.Client.SectionKey<global::GameServer.Modules.Economy.Protocol.V1.WalletState> BonusWallet =");
        text.ShouldContain("Client-visible sections of the game:");
    }

    [Fact]
    public async Task A_message_the_client_does_not_have_fails_naming_section_and_message()
    {
        var model = await FrameworkModelAsync();

        Should.Throw<WireGenException>(() => SectionRenderer.Render(
                [new ContractSection("card", "fixture", "game", "fixture.v1.CardState")], model, "G", "S", "game", "0.7.0", []))
            .Message.ShouldBe(
                "[section card] message fixture.v1.CardState is in neither the listed protos nor the framework protos. Fix: list its .proto in " +
                "wiregen.json protos, or map the section to a message the client has (FG-028).");
    }

    [Fact]
    public async Task Colliding_members_fail_naming_both()
    {
        var model = await FrameworkModelAsync();

        Should.Throw<WireGenException>(() => SectionRenderer.Render(
                [new ContractSection("a.b", "m", "game", null), new ContractSection("a_b", "m", "game", null)], model, "G", "S", "game", "0.7.0", []))
            .Message.ShouldBe("[section a.b] (id constant) and [section a_b] (id constant) both generate the member S.ABId. Fix: rename one of them (FG-026).");
        Should.Throw<WireGenException>(() => SectionRenderer.Render(
                [new ContractSection("table", "m", "game", "gameserver.economy.v1.WalletState")], model, "G", "S", "game", "0.7.0", []))
            .Message.ShouldContain("[sections] (the section table) and [section table] both generate the member S.Table.");
        Should.Throw<WireGenException>(() => SectionRenderer.Render(
                [new ContractSection("wallet", "m", "game", "gameserver.economy.v1.WalletState"), new ContractSection("wallet_id", "m", "game", "gameserver.economy.v1.WalletState")],
                model,
                "G",
                "S",
                "game",
                "0.7.0",
                []))
            .Message.ShouldContain("[section wallet] (id constant) and [section wallet_id] both generate the member S.WalletId.");
    }

    private async Task<ProtoModel> FrameworkModelAsync()
    {
        var protoc = Protoc.Locate(Repo.Tool);
        var framework = protoc.FrameworkProtos();
        var set = await protoc.DescribeAsync(protoc.ImportRoots([]), framework, _tree.Folder("temp-" + Guid.NewGuid().ToString("N")));
        return ProtoModel.Build(set, [], framework.Select(f => f.Relative));
    }
}
