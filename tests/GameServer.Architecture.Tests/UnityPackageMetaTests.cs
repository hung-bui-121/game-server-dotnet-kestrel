using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// FR-034, FR-035, US5-1 (slice 014 T063): the Unity package keeps its identity. Every file and folder has a <c>.meta</c>, the GUIDs
/// recorded before the slice never change (a changed GUID breaks every scene and prefab reference in a game), GUIDs are unique, and
/// the package version and assembly definition names stay.
/// </summary>
public sealed partial class UnityPackageMetaTests
{
    private const string PackageRelative = "sdk/unity/com.gameserver.client";

    private static readonly string PackageRoot = Path.Combine(RepositoryTooling.Root, PackageRelative);

    [Fact]
    public void Every_file_and_folder_of_the_package_has_a_meta()
    {
        MissingMetas(PackageRoot).ShouldBeEmpty();
    }

    [Fact]
    public void The_recorded_guids_are_unchanged()
    {
        GuidFaults(PackageRoot, RecordedGuids()).ShouldBeEmpty();
    }

    [Fact]
    public void Every_guid_is_unique()
    {
        DuplicateGuids(PackageRoot).ShouldBeEmpty();
    }

    [Fact]
    public void Package_version_and_assembly_names_are_unchanged()
    {
        var package = JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot, "package.json")))!;
        package["name"]!.GetValue<string>().ShouldBe("com.gameserver.client");
        package["version"]!.GetValue<string>().ShouldBe("0.7.0");
        AsmdefName("Runtime/GameServer.Client.asmdef").ShouldBe("GameServer.Client");
        AsmdefName("Editor/GameServer.Client.Editor.asmdef").ShouldBe("GameServer.Client.Editor");
    }

    [Fact]
    public void Each_rule_fails_on_a_seeded_fault_in_a_copy()
    {
        var copy = Directory.CreateTempSubdirectory("unity-package-").FullName;
        try
        {
            CopyTree(PackageRoot, copy);
            MissingMetas(copy).ShouldBeEmpty();
            GuidFaults(copy, RecordedGuids()).ShouldBeEmpty();

            var core = Path.Combine(copy, "Runtime", "Core", "GameServerClient.cs.meta");
            File.Delete(core);
            File.WriteAllText(Path.Combine(copy, "Runtime", "Stray.cs"), "// no meta");
            var guidFile = Path.Combine(copy, "Runtime", "Generated", "Iap.cs.meta");
            File.WriteAllText(guidFile, GuidLine().Replace(File.ReadAllText(guidFile), "guid: 0f5dd156de6c74544ad7c28ebb3faeb5"));
            var duplicate = Path.Combine(copy, "Runtime", "Core", "SectionKey.cs.meta");
            File.WriteAllText(duplicate, GuidLine().Replace(File.ReadAllText(duplicate), "guid: 4315f5218d5cf8343a5fc6a30ac43d55"));

            MissingMetas(copy).ShouldBe(["Runtime/Core/GameServerClient.cs", "Runtime/Stray.cs"], ignoreOrder: true);
            GuidFaults(copy, RecordedGuids()).ShouldBe(
            [
                $"{PackageRelative}/Runtime/Core/GameServerClient.cs.meta: missing (recorded GUID 96579d2e25954cc4f97f8ee4d5bac198)",
                $"{PackageRelative}/Runtime/Generated/Iap.cs.meta: GUID 0f5dd156de6c74544ad7c28ebb3faeb5, recorded 0f5dd156de6c74544ad7c28ebb3faeb4",
            ]);
            DuplicateGuids(copy).ShouldBe(["4315f5218d5cf8343a5fc6a30ac43d55: Runtime/Core/SectionKey.cs.meta, Runtime/Core/SectionStore.cs.meta"]);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>Entries Unity imports (it skips names starting with '.' and ending with '~') that have no sibling <c>.meta</c>.</summary>
    internal static IEnumerable<string> MissingMetas(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(entry => !entry.EndsWith(".meta", StringComparison.Ordinal) && Imported(root, entry))
            .Where(entry => !File.Exists(entry + ".meta"))
            .Select(entry => Relative(root, entry))
            .Order(StringComparer.Ordinal);

    internal static IEnumerable<string> GuidFaults(string root, IReadOnlyList<(string Guid, string Path)> recorded)
    {
        foreach (var (guid, path) in recorded)
        {
            var file = Path.Combine(root, path[(PackageRelative.Length + 1)..]);
            var actual = File.Exists(file) ? GuidOf(file) : null;
            if (actual is null)
            {
                yield return $"{path}: missing (recorded GUID {guid})";
            }
            else if (actual != guid)
            {
                yield return $"{path}: GUID {actual}, recorded {guid}";
            }
        }
    }

    internal static IEnumerable<string> DuplicateGuids(string root) =>
        Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
            .Select(file => (Guid: GuidOf(file), File: Relative(root, file)))
            .GroupBy(meta => meta.Guid, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(meta => meta.File).Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal);

    private static IReadOnlyList<(string Guid, string Path)> RecordedGuids() =>
        File.ReadAllLines(Path.Combine(RepositoryTooling.Root, "tests", "GameServer.Architecture.Tests", "TestData", "unity-package-guids.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', 2))
            .Select(parts => (parts[0], parts[1]))
            .ToArray();

    private static string? GuidOf(string metaFile) => GuidLine().Match(File.ReadAllText(metaFile)) is { Success: true } match ? match.Groups[1].Value : null;

    private static bool Imported(string root, string entry) =>
        Relative(root, entry).Split('/').All(part => !part.StartsWith('.') && !part.EndsWith('~'));

    private static string Relative(string root, string entry) => Path.GetRelativePath(root, entry).Replace('\\', '/');

    private static string AsmdefName(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot, relative)))!["name"]!.GetValue<string>();

    private static void CopyTree(string source, string target)
    {
        foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, folder)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    [GeneratedRegex(@"(?m)^guid: ([0-9a-f]{32})\r?$")]
    private static partial Regex GuidLine();
}
