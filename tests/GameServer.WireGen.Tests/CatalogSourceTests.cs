using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T042 (FR-030, US2-1): the catalog from a committed export or from the host's export mode.</summary>
public sealed class CatalogSourceTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_committed_export_is_read()
    {
        var contract = CatalogSource.Read(Repo.TestData("sample-export.json"));

        contract.FrameworkVersion.ShouldBe("0.7.0");
        contract.ProtobufVersion.ShouldBe("3.36.2");
        contract.Routes.Select(r => r.Name).ShouldBe(["battle.end", "cheat.grant", "gm.sso_start", "platform.health_live", "platform.receipt"]);
        var health = contract.Routes.Single(r => r.Name == "platform.health_live");
        health.Methods.ShouldBeEmpty();
        health.Surface.ShouldBe("Host");
        contract.Routes.Single(r => r.Name == "battle.end").Operation.ShouldBe("battle.end");
        contract.Sections.Single(s => s.Id == "wallet").Message.ShouldBe("gameserver.economy.v1.WalletState");
    }

    [Fact]
    public void An_unknown_major_is_refused_naming_the_file()
    {
        var file = _tree.Write("export.json", """{ "format": "gameserver.wire-contract/2", "frameworkVersion": "0.7.0", "protobufVersion": "3.36.2" }""");

        Should.Throw<WireGenException>(() => CatalogSource.Read(file)).Message
            .ShouldContain($"[catalog] {file} has format 'gameserver.wire-contract/2', this generator reads gameserver.wire-contract/1 (major 1).");
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_a_minor_suffix_is_read()
    {
        var file = _tree.Write("export.json", """
            {
              "format": "gameserver.wire-contract/1.1",
              "frameworkVersion": "0.7.0",
              "protobufVersion": "3.36.2",
              "configFiles": ["a.json"],
              "routes": [ { "name": "a.b", "methods": ["POST"], "template": "/v1/a", "profile": "Player", "surface": "PlayerApi",
                            "owner": "a", "origin": "game", "future": { "x": 1 } } ]
            }
            """);

        CatalogSource.Read(file).Routes.ShouldHaveSingleItem().Name.ShouldBe("a.b");
    }

    [Fact]
    public void A_missing_property_names_it()
    {
        var file = _tree.Write("export.json", """{ "format": "gameserver.wire-contract/1", "frameworkVersion": "0.7.0" }""");

        Should.Throw<WireGenException>(() => CatalogSource.Read(file)).Message.ShouldContain("property 'protobufVersion' is missing");
    }

    [Fact]
    public async Task The_host_export_yields_the_fixture_routes()
    {
        var config = HostConfig("Local");

        var contract = await CatalogSource.LoadAsync(config, DotnetHost.Resolve(null), Repo.Configuration, noBuild: true, _tree.Folder("temp"));

        contract.Routes.ShouldContain(r => r.Name == "fixture.save" && r.Origin == "game" && r.Template == "/api/v1/fixture/save");
        contract.Routes.ShouldContain(r => r.Name == "identity.login_guest" && r.Origin == "framework");
        contract.FrameworkVersion.ShouldBe(Repo.Tool.FrameworkVersion);
        Directory.EnumerateFileSystemEntries(_tree.Path("temp")).ShouldBeEmpty("the temp export is deleted after reading");
    }

    [Fact]
    public async Task A_host_that_exits_1_is_an_error_with_the_host_lines()
    {
        var config = HostConfig("Nope");

        var ex = await Should.ThrowAsync<WireGenException>(() =>
            CatalogSource.LoadAsync(config, DotnetHost.Resolve(null), Repo.Configuration, noBuild: true, _tree.Folder("temp")));

        ex.Lines[0].ShouldStartWith($"[catalog] the export of {config.HostProject} (--environment Nope) exited with code 1:");
        ex.Lines.ShouldContain(l => l.Contains("GameServer:EnvironmentId '' is missing or invalid", StringComparison.Ordinal));
    }

    private WireGenConfig HostConfig(string environment) =>
        new(
            _tree.Path("wiregen.json"),
            Repo.Path("tests/ConsumerFixture/src/Fixture.Host/Fixture.Host.csproj"),
            environment,
            null,
            "game",
            [],
            [],
            _tree.Root,
            _tree.Root,
            OutputLayout.Flat,
            LineEnding.Lf,
            "Fixture.Client",
            "FixtureRoutes",
            "FixtureSections");
}
