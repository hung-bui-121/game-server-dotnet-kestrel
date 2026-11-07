using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 018 T027 (FR-041..FR-043, FR-046, FR-047, A9): the GM console workspace <c>gm-console/</c> read as files — no Node in
/// <c>dotnet test</c>. Exact dependency versions, a version-3 lockfile, no tracked bundle or <c>node_modules</c>, no project
/// that runs npm, and a shell package that imports nothing outside itself. The versions are in <see cref="VersionTests"/>.
/// </summary>
public sealed partial class GmConsoleWorkspaceTests
{
    public const string Workspace = "gm-console";

    /// <summary>The <c>package.json</c> files of the workspace whose versions and dependencies are pinned.</summary>
    public static readonly string[] PackageFiles = ["gm-console/package.json", "gm-console/packages/shell/package.json", "gm-console/app/package.json"];

    private static readonly string[] DependencySections = ["dependencies", "devDependencies", "peerDependencies", "overrides"];

    private const string ShellSource = "gm-console/packages/shell/src";

    [Fact]
    public void Every_dependency_of_the_workspace_is_an_exact_version()
    {
        var failures = PackageFiles.SelectMany(file => InexactDependencies(file, File.ReadAllText(Path.Combine(RepositoryTooling.Root, file))));

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void An_inexact_dependency_names_the_file_the_package_and_the_rule()
    {
        const string json = """{ "name": "x", "dependencies": { "react": "^19.3.1", "a": "19.3.1", "b": "latest", "c": "1.2.3-rc.1" }, "devDependencies": { "d": "~1.0.0", "@gameserver/gm-shell": "*" } }""";

        InexactDependencies("gm-console/app/package.json", json).ShouldBe(
        [
            "gm-console/app/package.json dependencies.react = '^19.3.1' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).",
            "gm-console/app/package.json dependencies.b = 'latest' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).",
            "gm-console/app/package.json dependencies.c = '1.2.3-rc.1' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).",
            "gm-console/app/package.json devDependencies.d = '~1.0.0' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).",
            "gm-console/app/package.json devDependencies.@gameserver/gm-shell = '*' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).",
        ]);
    }

    /// <summary>
    /// Review T068 M2 (dependency confusion): the app's <c>@gameserver/gm-shell</c> dependency is the declared framework version and the
    /// lockfile resolves it to the workspace folder (a link), never a registry; <c>.npmrc</c> points the <c>@gameserver</c> scope at an
    /// invalid registry, so a missing link fails instead of downloading a public package of that name.
    /// </summary>
    [Fact]
    public void The_app_uses_the_workspace_shell_of_the_declared_version_and_never_a_registry()
    {
        var root = Path.Combine(RepositoryTooling.Root, Workspace);
        WorkspaceLinkFailures(
                RepositoryTooling.DeclaredFrameworkVersion(),
                File.ReadAllText(Path.Combine(root, "app", "package.json")),
                File.ReadAllText(Path.Combine(root, "package-lock.json")),
                File.ReadAllText(Path.Combine(root, ".npmrc")))
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_shell_dependency_that_could_come_from_a_registry_is_reported()
    {
        const string app = """{ "dependencies": { "@gameserver/gm-shell": "0.7.1" } }""";
        const string lockfile = """{ "lockfileVersion": 3, "packages": { "": { "version": "0.7.0" }, "app": { "version": "0.7.0", "dependencies": { "@gameserver/gm-shell": "0.7.1" } }, "packages/shell": { "version": "0.6.0" }, "node_modules/@gameserver/gm-shell": { "version": "0.7.1", "resolved": "https://registry.npmjs.org/@gameserver/gm-shell/-/gm-shell-0.7.1.tgz" } } }""";

        WorkspaceLinkFailures("0.7.0", app, lockfile, "engine-strict=true\n").ShouldBe(
        [
            "gm-console/app/package.json depends on @gameserver/gm-shell '0.7.1', not the framework version '0.7.0' (bump them together).",
            "gm-console/package-lock.json app depends on @gameserver/gm-shell '0.7.1', not '0.7.0': run npm install in gm-console after the bump.",
            "gm-console/package-lock.json packages/shell version is '0.6.0', not '0.7.0': run npm install in gm-console after the bump.",
            "gm-console/package-lock.json resolves node_modules/@gameserver/gm-shell to 'https://registry.npmjs.org/@gameserver/gm-shell/-/gm-shell-0.7.1.tgz', not a link to packages/shell (dependency confusion).",
            "gm-console/.npmrc must contain '@gameserver:registry=https://registry.invalid/' so the scope never resolves from a registry.",
        ]);
    }

    [Fact]
    public void The_lockfile_is_committed_in_version_3()
    {
        var lockfile = Path.Combine(RepositoryTooling.Root, Workspace, "package-lock.json");

        File.Exists(lockfile).ShouldBeTrue("gm-console/package-lock.json is missing: run npm install in gm-console and commit the lockfile (FR-040).");
        using var document = JsonDocument.Parse(File.ReadAllText(lockfile));
        document.RootElement.GetProperty("lockfileVersion").GetInt32().ShouldBe(3, "gm-console/package-lock.json must be lockfile version 3 (npm ≥ 9, FR-040).");
    }

    [Fact]
    public async Task No_build_output_or_dependency_folder_is_tracked()
    {
        var git = RepositoryTooling.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        Assert.SkipUnless(IsGitCheckout() && git is not null, "Not a git checkout (or git not on PATH): the tracked-file rule is checked in a checkout.");

        var result = await RepositoryTooling.RunAsync(git!, ["ls-files", "--", Workspace], RepositoryTooling.Root, TimeSpan.FromMinutes(1));
        result.ExitCode.ShouldBe(0, result.All);

        TrackedBundleFiles(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ShouldBeEmpty();
    }

    [Fact]
    public void A_tracked_bundle_or_dependency_file_is_reported()
    {
        TrackedBundleFiles(["gm-console/app/src/main.tsx", "gm-console/app/dist/index.html", "gm-console/node_modules/react/index.js", "gm-console/distance.md"])
            .ShouldBe(
            [
                "gm-console/app/dist/index.html is tracked: build output and node_modules are never committed (FR-042, .gitignore).",
                "gm-console/node_modules/react/index.js is tracked: build output and node_modules are never committed (FR-042, .gitignore).",
            ]);
    }

    [Fact]
    public async Task No_dotnet_project_file_runs_npm_or_node()
    {
        var git = RepositoryTooling.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        Assert.SkipUnless(IsGitCheckout() && git is not null, "Not a git checkout (or git not on PATH): the project files are listed by git.");

        var result = await RepositoryTooling.RunAsync(git!, ["ls-files", "--", "*.csproj", "*.props", "*.targets"], RepositoryTooling.Root, TimeSpan.FromMinutes(1));
        result.ExitCode.ShouldBe(0, result.All);
        var files = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        files.ShouldContain("Directory.Build.props");

        files.SelectMany(file => NodeMentions(file, File.ReadAllText(Path.Combine(RepositoryTooling.Root, file)))).ShouldBeEmpty();
    }

    [Fact]
    public void A_project_file_that_runs_npm_or_node_is_reported()
    {
        NodeMentions("x.csproj", "<Target Name=\"Ui\"><Exec Command=\"npm ci\" /></Target>").ShouldHaveSingleItem();
        NodeMentions("x.targets", "<Exec Command=\"node build.mjs\" />").ShouldHaveSingleItem();
        NodeMentions("x.props", "<None Include=\"nodes.txt\" /><Compile Include=\"Npm2.cs\" />").ShouldBeEmpty();
    }

    [Fact]
    public void The_shell_imports_only_its_own_source_and_packages()
    {
        var shell = Path.Combine(RepositoryTooling.Root, ShellSource);
        Directory.Exists(shell).ShouldBeTrue(ShellSource + " is missing.");
        var packageRoot = Path.GetDirectoryName(shell)!;

        var failures = Directory.EnumerateFiles(shell, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".ts", StringComparison.Ordinal) || file.EndsWith(".tsx", StringComparison.Ordinal))
            .SelectMany(file => ForeignImports(file, File.ReadAllText(file), packageRoot));

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void A_shell_import_from_the_app_or_outside_the_package_is_reported()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws", "packages", "shell");
        var file = Path.Combine(root, "src", "pages", "Page.tsx");
        const string source = """
            import { a } from '../components/A';
            import type { B } from "../../src/types";
            import x from '../../../../app/src/main';
            export { y } from '../../../docs/api/gm-openapi.json';
            import '@gameserver/gm-console-default';
            const z = await import('../../../app/src/z');
            import { useState } from 'react';
            """;

        ForeignImports(file, source, root).Select(f => f.Split(" imports ", 2)[1]).ShouldBe(
        [
            "'../../../../app/src/main': the shell imports only its own source and its peer packages (FR-041).",
            "'../../../docs/api/gm-openapi.json': the shell imports only its own source and its peer packages (FR-041).",
            "'@gameserver/gm-console-default': the shell imports only its own source and its peer packages (FR-041).",
            "'../../../app/src/z': the shell imports only its own source and its peer packages (FR-041).",
        ]);
    }

    /// <summary>Every dependency value that is not an exact MAJOR.MINOR.PATCH version (review T068 m8: no prerelease either).</summary>
    internal static IEnumerable<string> InexactDependencies(string file, string json)
    {
        using var document = JsonDocument.Parse(json);
        var failures = new List<string>();
        foreach (var section in DependencySections)
        {
            if (!document.RootElement.TryGetProperty(section, out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                var value = dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString()! : dependency.Value.GetRawText();
                if (!ExactVersion().IsMatch(value))
                {
                    failures.Add($"{file} {section}.{dependency.Name} = '{value}' is not an exact version (FR-043: pin MAJOR.MINOR.PATCH, no range, prerelease, tag or URL).");
                }
            }
        }

        return failures;
    }

    /// <summary>The app's shell dependency, the lockfile's workspace entries and the scope registry guard (review T068 M2).</summary>
    internal static IEnumerable<string> WorkspaceLinkFailures(string declared, string appPackageJson, string lockfileJson, string npmrc)
    {
        const string Shell = "@gameserver/gm-shell";
        using var app = JsonDocument.Parse(appPackageJson);
        using var lockfile = JsonDocument.Parse(lockfileJson);
        var packages = lockfile.RootElement.GetProperty("packages");
        string? Text(JsonElement element, params string[] path)
        {
            foreach (var name in path)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                {
                    return null;
                }
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        var failures = new List<string>();
        var appSpec = Text(app.RootElement, "dependencies", Shell);
        if (appSpec != declared)
        {
            failures.Add($"gm-console/app/package.json depends on {Shell} '{appSpec}', not the framework version '{declared}' (bump them together).");
        }

        var lockedSpec = Text(packages, "app", "dependencies", Shell);
        if (lockedSpec != declared)
        {
            failures.Add($"gm-console/package-lock.json app depends on {Shell} '{lockedSpec}', not '{declared}': run npm install in gm-console after the bump.");
        }

        foreach (var entry in new[] { "", "app", "packages/shell" })
        {
            var version = Text(packages, entry, "version");
            if (version != declared)
            {
                failures.Add($"gm-console/package-lock.json {(entry == "" ? "root" : entry)} version is '{version}', not '{declared}': run npm install in gm-console after the bump.");
            }
        }

        var link = Text(packages, "node_modules/" + Shell, "link");
        var resolved = Text(packages, "node_modules/" + Shell, "resolved");
        if (link != "true" || resolved != "packages/shell")
        {
            failures.Add($"gm-console/package-lock.json resolves node_modules/{Shell} to '{resolved}', not a link to packages/shell (dependency confusion).");
        }

        if (!npmrc.Split('\n').Select(line => line.Trim()).Contains("@gameserver:registry=https://registry.invalid/"))
        {
            failures.Add("gm-console/.npmrc must contain '@gameserver:registry=https://registry.invalid/' so the scope never resolves from a registry.");
        }

        return failures;
    }

    /// <summary>Tracked paths under a <c>node_modules</c> or <c>dist</c> folder.</summary>
    internal static IEnumerable<string> TrackedBundleFiles(IEnumerable<string> trackedPaths) =>
        trackedPaths
            .Where(path => path.Split('/').SkipLast(1).Any(segment => segment is "node_modules" or "dist"))
            .Select(path => $"{path} is tracked: build output and node_modules are never committed (FR-042, .gitignore).");

    /// <summary>Lines of a .NET project file that run npm, npx or node (FR-046: dotnet build/test never need Node).</summary>
    internal static IEnumerable<string> NodeMentions(string file, string text) =>
        text.Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .Where(l => NodeCommand().IsMatch(l.line))
            .Select(l => $"{file}:{l.number} mentions npm/node: no .NET project invokes Node (FR-046). Line: {l.line.Trim()}");

    /// <summary>Import specifiers of <paramref name="file"/> that leave <paramref name="packageRoot"/> or name the default app.</summary>
    internal static IEnumerable<string> ForeignImports(string file, string source, string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        foreach (Match match in ImportSpecifier().Matches(source))
        {
            var specifier = match.Groups["spec"].Value;
            var foreign = specifier.StartsWith('.')
                ? !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, specifier)).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                  || specifier.Contains("/app/", StringComparison.Ordinal)
                : specifier.StartsWith("@gameserver/gm-console-default", StringComparison.Ordinal);
            if (foreign)
            {
                yield return $"{file} imports '{specifier}': the shell imports only its own source and its peer packages (FR-041).";
            }
        }
    }

    internal static JsonElement ReadPackage(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryTooling.Root, file)));
        return document.RootElement.Clone();
    }

    private static bool IsGitCheckout() =>
        Directory.Exists(Path.Combine(RepositoryTooling.Root, ".git")) || File.Exists(Path.Combine(RepositoryTooling.Root, ".git"));

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactVersion();

    [GeneratedRegex(@"(?i)(?<![\w.-])(npm|npx|node(\.exe)?)\s")]
    private static partial Regex NodeCommand();

    [GeneratedRegex("""(?:\bfrom\s*|\bimport\s*\(?\s*)['"](?<spec>[^'"]+)['"]""")]
    private static partial Regex ImportSpecifier();
}
