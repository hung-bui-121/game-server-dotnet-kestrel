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
    private static readonly string Root = RepositoryRoot();

    /// <summary>Platform internals the kit may use: the runner's context, the commit rules and the loaded-aggregate constructor.</summary>
    private static readonly string[] AllowedInternalTypes =
        ["GameServer.Platform.Commands.CommandContext", "GameServer.Platform.Commands.CommitRules"];

    private const string AllowedInternalMember = "GameServer.Platform.Aggregates.LoadedAggregate::.ctor";

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
        var used = PlatformInternalsUsedByKit();

        used.ShouldContain(AllowedInternalTypes[0], "the rule must see the kit's use of the runner's context");
        used.ShouldContain(AllowedInternalTypes[1]);
        used.ShouldContain(AllowedInternalMember);
        used.Where(u => !AllowedInternalTypes.Any(t => u == t || u.StartsWith(t + "::", StringComparison.Ordinal)) && u != AllowedInternalMember)
            .ShouldBeEmpty(
                "GameServer.TestKit may use only CommandContext, CommitRules and the LoadedAggregate constructor of GameServer.Platform's " +
                "internals (InternalsVisibleTo for the command-context double, FR-025); anything else needs a review and a new entry here");
    }

    /// <summary>Non-public Platform types and members the kit's IL refers to (type references and member references).</summary>
    private static SortedSet<string> PlatformInternalsUsedByKit()
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(TestKit.Location)!);
        using var module = ModuleDefinition.ReadModule(TestKit.Location, new ReaderParameters { AssemblyResolver = resolver });
        var platform = Platform.GetName().Name;
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
