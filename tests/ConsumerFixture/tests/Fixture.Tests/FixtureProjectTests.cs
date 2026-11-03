using System.Xml.Linq;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// SC-002: this test project consumes the framework like a game — the kit (and the SDK, aliased, for tests that call it), no linked
/// framework test file, no sample.
/// </summary>
public sealed class FixtureProjectTests
{
    private const string Sdk = "$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj";

    [Fact]
    public void The_test_project_references_only_the_kit_the_sdk_and_the_fixture_projects()
    {
        var text = File.ReadAllText(ProjectFile());
        var project = XDocument.Parse(text);

        var references = project.Descendants("ProjectReference").Select(r => ((string)r.Attribute("Include")!).Replace('\\', '/')).ToArray();

        references.ShouldBe(
            [
                "$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj",
                Sdk,
                "../../src/Fixture.Game/Fixture.Game.csproj",
                "../../src/Fixture.Host/Fixture.Host.csproj",
            ],
            ignoreOrder: true);
        var sdk = project.Descendants("ProjectReference").Single(r => ((string)r.Attribute("Include")!).Replace('\\', '/') == Sdk);
        ((string?)sdk.Attribute("Aliases")).ShouldBe("sdk", "the SDK's generated types collide with the server's without an alias");
        project.Descendants("Compile").ShouldBeEmpty("a game links no framework source file (tests/Shared is framework-only)");
        text.ShouldNotContain("tests/Shared");
        text.ShouldNotContain("Sample");
    }

    private static string ProjectFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Fixture.Tests.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Fixture.Tests.csproj not found above " + AppContext.BaseDirectory);
    }
}
