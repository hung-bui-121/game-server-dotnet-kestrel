using System.Reflection;
using System.Xml.Linq;
using GameServer.Platform.Commands;
using GameServer.TestKit;
using Mono.Cecil;
using NetArchTest.Rules;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T032 (FR-010, FR-014): the consumer test kit stays a consumer-referenceable library — no test framework, sample
/// or test project behind it, no host or framework project in front of it, and Platform internals limited to what the
/// command-context double shares with the runner (review rule).
/// </summary>
public sealed class TestKitArchitectureTests
{
    private const string KitProject = "src/GameServer.TestKit/GameServer.TestKit.csproj";

    private static readonly Assembly TestKit = typeof(CommandContextDouble).Assembly;
    private static readonly Assembly Platform = typeof(CommandRunner).Assembly;
    private static readonly Assembly PlatformWeb = typeof(GameServer.Platform.Web.Aggregates.AggregateWireMapperRegistry).Assembly;
    private static readonly string Root = RepositoryRoot();

    /// <summary>
    /// Platform internals the kit may use: the runner's context, the commit rules and the loaded-aggregate constructor; and the
    /// config declaration engine with the registry's required-file lines, so ConfigDeclarationCheck reports the host's lines
    /// (slice 015, FR-018).
    /// </summary>
    private static readonly string[] AllowedInternalTypes =
    [
        "GameServer.Platform.Commands.CommandContext",
        "GameServer.Platform.Commands.CommitRules",
        "GameServer.Platform.Config.ConfigDeclarationRunner",
        "GameServer.Platform.Config.ConfigDeclarationResult",
        "GameServer.Platform.Config.CatalogBinding",
    ];

    private static readonly string[] AllowedInternalMembers =
    [
        "GameServer.Platform.Aggregates.LoadedAggregate::.ctor",
        "GameServer.Platform.Registry.ModuleRegistry::ValidateConfig",

        // TestGameConfigStatus (slice 015 T071 G2): the status a startup check gets, in the state the host would publish.
        "GameServer.Platform.GameConfigStatus::.ctor",
        "GameServer.Platform.GameConfigStatus::PublishLoaded",
        "GameServer.Platform.GameConfigStatus::PublishFailed",
    ];

    /// <summary>
    /// Platform.Web internals the kit may use: the member tree of aggregate states shared with the startup hidden-member check (slice 014,
    /// FG-027) and the startup-failure marker of LocalFlavorFactory (slice 015).
    /// </summary>
    private static readonly string[] AllowedWebInternalTypes =
    [
        "GameServer.Platform.Web.Aggregates.StateMembers",
        "GameServer.Platform.Web.Aggregates.StateMember",
        "GameServer.Platform.Web.Aggregates.StateNode",
        "GameServer.Platform.Web.Aggregates.StateLeaf",
        "GameServer.Platform.Web.Aggregates.StateObject",
        "GameServer.Platform.Web.Aggregates.StateNullable",
        "GameServer.Platform.Web.Aggregates.StateCollection",
        "GameServer.Platform.Web.Aggregates.StateDictionary",
        "GameServer.Platform.Web.Aggregates.StateUnfillable",

        // LocalFlavorFactory (slice 015 T071 G3): a startup failure is rethrown to the test instead of exit code 1.
        "GameServer.Platform.Web.Hosting.StartupFailureMode",
    ];

    [Fact]
    public void The_kit_references_no_test_framework_sample_or_test_assembly()
    {
        var references = TestKit.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        references.ShouldContain(Platform.GetName().Name!, "the reference walk must see a real reference");

        references.Where(IsForbiddenAssembly).ShouldBeEmpty("GameServer.TestKit must not reference xUnit, Shouldly, a sample or a test assembly (FR-010)");
        var result = Types.InAssembly(TestKit).ShouldNot().HaveDependencyOnAny("Xunit", "Shouldly", "Sample", "SampleGame").GetResult();
        result.IsSuccessful.ShouldBeTrue($"Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");

        var project = XDocument.Load(Path.Combine(Root, KitProject));
        project.Descendants("PackageReference").Select(p => (string)p.Attribute("Include")!)
            .Where(name => name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Shouldly", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty("GameServer.TestKit must not force a unit-test framework on the consumer (FR-010)");
        project.Descendants("ProjectReference").Select(p => Normalize((string)p.Attribute("Include")!))
            .Where(path => path.StartsWith("../../tests/", StringComparison.Ordinal)
                || path.StartsWith("../../samples/", StringComparison.Ordinal)
                || path.StartsWith("../../modules/", StringComparison.Ordinal))
            .ShouldBeEmpty("GameServer.TestKit references framework src projects only (FR-010)");
    }

    [Fact]
    public void No_project_outside_tests_references_the_kit()
    {
        var projects = SolutionProjects().Where(p => !p.StartsWith("tests/", StringComparison.Ordinal) && p != KitProject).ToArray();
        projects.ShouldContain("samples/SampleHost/SampleHost.csproj", "the rule must see the sample host");

        var offenders = projects.Where(ReferencesKit).ToArray();

        offenders.ShouldBeEmpty("hosts, framework, module, template and tool projects never reference the test kit (FR-010, FR-014)");
    }

    [Fact]
    public void The_kit_uses_only_the_platform_internals_shared_with_the_runner()
    {
        var used = InternalsUsedByKit(Platform);

        used.ShouldContain(AllowedInternalTypes[0], "the rule must see the kit's use of the runner's context");
        used.ShouldContain(AllowedInternalTypes[1]);
        used.ShouldContain(AllowedInternalTypes[2], "the rule must see the kit's use of the config declaration engine");
        used.ShouldContain(AllowedInternalMembers[0]);
        used.ShouldContain(AllowedInternalMembers[1]);
        used.Where(u => !AllowedInternalTypes.Any(t => u == t || u.StartsWith(t + "::", StringComparison.Ordinal)) && !AllowedInternalMembers.Contains(u))
            .ShouldBeEmpty(
                "GameServer.TestKit may use only CommandContext, CommitRules, the LoadedAggregate constructor, the config declaration " +
                "engine, ModuleRegistry.ValidateConfig and the GameConfigStatus constructor and publish methods of GameServer.Platform's " +
                "internals (InternalsVisibleTo for the command-context double, FR-025, the config check, FR-018, and TestGameConfigStatus, " +
                "FG-023); anything else needs a review and a new entry here");
    }

    [Fact]
    public void The_kit_uses_only_the_state_member_tree_of_the_web_platform_internals()
    {
        var used = InternalsUsedByKit(PlatformWeb);

        used.Where(u => !AllowedWebInternalTypes.Any(t => u == t || u.StartsWith(t + "::", StringComparison.Ordinal)))
            .ShouldBeEmpty(
                "GameServer.TestKit may use only the state member tree (StateMembers and its node types) of GameServer.Platform.Web's " +
                "internals (InternalsVisibleTo for the conformance check, FG-027); anything else needs a review and a new entry here");
    }

    /// <summary>Non-public types and members of <paramref name="target"/> the kit's IL refers to (type references and member references).</summary>
    private static SortedSet<string> InternalsUsedByKit(Assembly target)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(TestKit.Location)!);
        using var module = ModuleDefinition.ReadModule(TestKit.Location, new ReaderParameters { AssemblyResolver = resolver });
        var platform = target.GetName().Name;
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in module.GetTypeReferences().Where(r => r.Scope.Name == platform))
        {
            if (reference.Resolve() is { } type && !IsVisible(type))
            {
                used.Add(type.FullName);
            }
        }

        foreach (var reference in module.GetMemberReferences().Where(r => r.DeclaringType.GetElementType().Scope.Name == platform))
        {
            var member = (IMemberDefinition?)reference.Resolve();
            var hidden = member switch
            {
                MethodDefinition method => !method.IsPublic && !method.IsFamily && !method.IsFamilyOrAssembly,
                FieldDefinition field => !field.IsPublic && !field.IsFamily && !field.IsFamilyOrAssembly,
                _ => false,
            };
            if (member is not null && (hidden || !IsVisible(member.DeclaringType)))
            {
                used.Add($"{member.DeclaringType.FullName}::{member.Name}");
            }
        }

        return used;
    }

    private static bool IsVisible(TypeDefinition type) =>
        type.IsPublic || (type.IsNestedPublic && IsVisible(type.DeclaringType));

    private static bool IsForbiddenAssembly(string name) =>
        name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Shouldly", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Sample", StringComparison.Ordinal)
        || name.EndsWith("Tests", StringComparison.Ordinal);

    private static IEnumerable<string> SolutionProjects() =>
        XDocument.Load(Path.Combine(Root, "GameServer.slnx")).Descendants("Project").Select(p => Normalize((string)p.Attribute("Path")!));

    private static bool ReferencesKit(string project) =>
        XDocument.Load(Path.Combine(Root, project)).Descendants("ProjectReference")
            .Any(r => Normalize((string)r.Attribute("Include")!).EndsWith("/GameServer.TestKit.csproj", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
    }
}
