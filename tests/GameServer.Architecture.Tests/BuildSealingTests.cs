using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T051 (FR-030..FR-032, US4): framework projects evaluate the same inside a consumer repository. The framework's
/// build inputs and project files are copied below a parent folder named <c>tests</c> that holds a hostile
/// <c>Directory.Build.props</c>, <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c>; every project must see
/// none of them (central package versions come from the framework's own file, review m12), and the test-project rule must
/// follow the framework root, not the absolute path; every project keeps the declared framework version (FR-050).
/// </summary>
public sealed class BuildSealingTests : IDisposable
{
    private const int MaxParallelEvaluations = 8;
    private const string HostileProps = "GameServerHostileProps";
    private const string HostileTargets = "GameServerHostileTargets";
    private const string HostilePackages = "GameServerHostilePackages";
    private const string HostileVersion = "0.0.1-hostile";

    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] BuildInputs =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "BannedSymbols.txt", ".editorconfig"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-seal-" + Guid.NewGuid().ToString("N"));

    private string Consumer => Path.Combine(_root, "tests", "consumer");

    private string Framework => Path.Combine(Consumer, "framework");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Framework_projects_below_a_hostile_tests_folder_see_no_consumer_build_file_and_keep_their_rules()
    {
        var dotnet = RepositoryTooling.Dotnet();
        Assert.SkipWhen(dotnet is null, "No dotnet executable (DOTNET_HOST_PATH, DOTNET_ROOT, PATH): the build seal check needs the SDK.");

        var projects = FrameworkProjects();
        projects.ShouldContain("tests/GameServer.Architecture.Tests/GameServer.Architecture.Tests.csproj", "the check must see a test project");
        projects.ShouldContain("src/GameServer.Core/GameServer.Core.csproj", "the check must see a framework project");
        CopyLayout(projects);

        var results = new ConcurrentDictionary<string, Evaluation>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelEvaluations, CancellationToken = TestContext.Current.CancellationToken },
            async (project, _) => results[project] = await EvaluateAsync(dotnet!, project));

        var declaredVersion = RepositoryTooling.DeclaredFrameworkVersion();
        var bannedSymbols = Path.Combine(Framework, "BannedSymbols.txt");
        var packagesProps = Path.Combine(Framework, "Directory.Packages.props");
        var frameworkPackages = FrameworkPackageVersions();
        foreach (var project in projects)
        {
            var evaluation = results[project];
            evaluation.Get(HostileTargets).ShouldBeEmpty($"{project}: a Directory.Build.targets above the framework root leaked into it (FR-030)");
            evaluation.Get(HostileProps).ShouldBeEmpty($"{project}: a Directory.Build.props above the framework root leaked into it");
            evaluation.Get(HostilePackages).ShouldBeEmpty($"{project}: a Directory.Packages.props above the framework root leaked into it");
            var packagesPath = evaluation.Get("DirectoryPackagesPropsPath");
            string.Equals(Path.GetFullPath(packagesPath), packagesProps, StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"{project} imports {packagesPath}, not the framework's Directory.Packages.props");
            evaluation.PackageVersions.ShouldBe(frameworkPackages, ignoreOrder: true, $"{project} resolves central package versions from the framework's file");
            evaluation.Get("Version").ShouldBe(declaredVersion, $"{project} carries the declared framework version (FR-050)");
            if (project.StartsWith("tests/", StringComparison.Ordinal))
            {
                evaluation.Get("IsTestProject").ShouldBe("true", $"{project} is under the framework's tests/ folder (FR-031)");
                evaluation.Get("IsFrameworkCode").ShouldBeEmpty(project);
            }
            else
            {
                evaluation.Get("IsTestProject").ShouldBeEmpty($"{project}: the parent folder named 'tests' must not make it a test project (FR-031)");
                evaluation.Get("IsFrameworkCode").ShouldBe("true", $"{project} keeps analyzers and banned APIs (FR-031)");
                evaluation.AdditionalFiles.ShouldContain(
                    path => string.Equals(Path.GetFullPath(path), bannedSymbols, StringComparison.OrdinalIgnoreCase),
                    $"{project} gets the framework's BannedSymbols.txt");
            }
        }
    }

    /// <summary><c>Include=Version</c> of every <c>PackageVersion</c> in the framework's <c>Directory.Packages.props</c>.</summary>
    private static string[] FrameworkPackageVersions() =>
        XDocument.Load(Path.Combine(RepositoryTooling.Root, "Directory.Packages.props")).Descendants("PackageVersion")
            .Select(p => $"{(string)p.Attribute("Include")!}={(string)p.Attribute("Version")!}")
            .ToArray();

    /// <summary>Every project of <c>GameServer.slnx</c> except the consumer fixture (its own props, like a game repository).</summary>
    private static string[] FrameworkProjects() =>
        XDocument.Load(Path.Combine(RepositoryTooling.Root, "GameServer.slnx")).Descendants("Project")
            .Select(p => ((string)p.Attribute("Path")!).Replace('\\', '/'))
            .Where(p => !p.StartsWith("tests/ConsumerFixture/", StringComparison.Ordinal))
            .ToArray();

    private void CopyLayout(IEnumerable<string> projects)
    {
        foreach (var file in BuildInputs.Concat(projects))
        {
            var target = Path.Combine(Framework, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(RepositoryTooling.Root, file), target);
        }

        File.WriteAllText(
            Path.Combine(Consumer, "Directory.Build.props"),
            $"<Project><PropertyGroup><{HostileProps}>leaked</{HostileProps}><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(Consumer, "Directory.Build.targets"),
            $"""
            <Project>
              <PropertyGroup><{HostileTargets}>leaked</{HostileTargets}></PropertyGroup>
              <Target Name="GameServerHostileTarget" BeforeTargets="Build"><Error Text="consumer Directory.Build.targets ran" /></Target>
            </Project>
            """);

        // A game's central versions: its own package and a conflicting pin of every framework package.
        var pins = string.Concat(FrameworkPackageVersions().Select(p => $"<PackageVersion Include=\"{p.Split('=')[0]}\" Version=\"{HostileVersion}\" />"));
        File.WriteAllText(
            Path.Combine(Consumer, "Directory.Packages.props"),
            $"<Project><PropertyGroup><{HostilePackages}>leaked</{HostilePackages}></PropertyGroup><ItemGroup>" +
            $"<PackageVersion Include=\"Game.Only.Package\" Version=\"1.0.0\" />{pins}</ItemGroup></Project>");
    }

    private async Task<Evaluation> EvaluateAsync(string dotnet, string project)
    {
        string[] arguments =
        [
            "msbuild", project, "-nologo", "-nodeReuse:false",
            "-getProperty:IsTestProject", "-getProperty:IsFrameworkCode", "-getProperty:Version", $"-getProperty:{HostileTargets}", $"-getProperty:{HostileProps}",
            $"-getProperty:{HostilePackages}", "-getProperty:DirectoryPackagesPropsPath", "-getItem:AdditionalFiles", "-getItem:PackageVersion",
        ];
        var result = await RepositoryTooling.RunAsync(dotnet, arguments, Framework, EvaluationTimeout);
        result.ExitCode.ShouldBe(0, $"dotnet msbuild {project} failed: {result.All}");

        // Several -getProperty/-getItem switches print one JSON object (V2): parse it, never match the text.
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
        var hasItems = json.RootElement.TryGetProperty("Items", out var items);
        var additionalFiles = hasItems && items.TryGetProperty("AdditionalFiles", out var files)
            ? files.EnumerateArray().Select(f => f.GetProperty("FullPath").GetString()!).ToArray()
            : [];
        var packageVersions = hasItems && items.TryGetProperty("PackageVersion", out var versions)
            ? versions.EnumerateArray().Select(v => $"{v.GetProperty("Identity").GetString()}={v.GetProperty("Version").GetString()}").ToArray()
            : [];
        return new Evaluation(properties, additionalFiles, packageVersions);
    }

    private sealed record Evaluation(IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> AdditionalFiles, IReadOnlyList<string> PackageVersions)
    {
        public string Get(string name) => Properties.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
