using System.Xml.Linq;
using Shouldly;

namespace Fixture.Tests;

/// <summary>SC-002: this test project consumes the framework like a game — the kit only, no linked framework test file, no sample.</summary>
public sealed class FixtureProjectTests
{
    [Fact]
    public void The_test_project_references_only_the_kit_and_the_fixture_projects()
    {
        var text = File.ReadAllText(ProjectFile());
        var project = XDocument.Parse(text);

        var references = project.Descendants("ProjectReference").Select(r => ((string)r.Attribute("Include")!).Replace('\\', '/')).ToArray();

        references.ShouldBe(
            ["$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj", "../../src/Fixture.Game/Fixture.Game.csproj", "../../src/Fixture.Host/Fixture.Host.csproj"],
            ignoreOrder: true);
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
