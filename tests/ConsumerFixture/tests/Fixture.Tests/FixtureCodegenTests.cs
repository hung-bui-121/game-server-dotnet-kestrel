using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// Slice 014 T053 (FR-021, FR-023..FR-028, SC-003, SC-004, US2-1..US2-7): the game runs the framework's generation step from its own
/// repository. The tool runs as a child process (<c>dotnet GameServer.WireGen.dll</c>, built by the solution build — a game
/// references no framework tool project); the host export runs once per class and feeds the <c>--catalog</c> cases.
/// </summary>
public sealed partial class FixtureCodegenTests(FixtureCodegenTests.Export export) : IClassFixture<FixtureCodegenTests.Export>, IDisposable
{
    private static readonly string[] Outputs = ["FixtureGame.cs", "FixtureRoutes.cs", "FixtureSections.cs"];

    private readonly string _temp = Directory.CreateTempSubdirectory("fixture-codegen-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public async Task The_committed_generation_verifies_against_the_host_export()
    {
        var result = await Tool.RunAsync("verify", "--config", Paths.Config, "--no-build", "--configuration", Paths.Configuration, "--dotnet", Paths.Dotnet);

        result.ExitCode.ShouldBe(0, result.All);
        result.Output.ShouldContain("verify: 3 files up to date");
    }

    [Fact]
    public async Task Two_checkout_paths_and_mixed_separators_give_identical_lf_bytes()
    {
        var first = Client("a");
        var second = Client("b");
        File.Delete(Path.Combine(first, "Assets/Generated/FixtureRoutes.cs"));
        File.Delete(Path.Combine(second, "Assets/Generated/FixtureRoutes.cs"));
        var plain = WriteConfig("config-a", Paths.Json(Paths.Protos), "../a");
        var mixed = WriteConfig(Path.Combine("deeper", "config-b"), Mixed(Paths.Protos), "..\\../b");

        (await GenerateAsync(plain)).ExitCode.ShouldBe(0);
        (await GenerateAsync(mixed)).ExitCode.ShouldBe(0);

        foreach (var output in Outputs)
        {
            var bytes = File.ReadAllBytes(Path.Combine(first, "Assets/Generated", output));
            File.ReadAllBytes(Path.Combine(second, "Assets/Generated", output)).ShouldBe(bytes, output);
            File.ReadAllBytes(Path.Combine(Paths.ClientGenerated, output)).ShouldBe(bytes, $"{output}: committed copy (SC-003, lf)");
            CheckClean(output, bytes);
        }
    }

    [Fact]
    public async Task Crlf_output_differs_only_in_line_endings_and_a_rerun_changes_no_timestamp()
    {
        var client = Client("c");
        var config = WriteConfig("config-c", Paths.Json(Paths.Protos), "../c", lineEndings: "crlf");

        (await GenerateAsync(config)).ExitCode.ShouldBe(0);
        var stamps = Outputs.ToDictionary(o => o, o => File.GetLastWriteTimeUtc(Path.Combine(client, "Assets/Generated", o)));
        var rerun = await GenerateAsync(config);

        rerun.Output.ShouldContain("generate: 0 written, 3 unchanged");
        foreach (var output in Outputs)
        {
            var path = Path.Combine(client, "Assets/Generated", output);
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            text.ShouldContain("\r\n");
            text.Replace("\r\n", "\n", StringComparison.Ordinal).ShouldNotContain("\r");
            Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))
                .ShouldBe(File.ReadAllBytes(Path.Combine(Paths.ClientGenerated, output)), $"{output} (SC-003, crlf)");
            File.GetLastWriteTimeUtc(path).ShouldBe(stamps[output]);
            CheckClean(output, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task A_meta_next_to_an_output_is_left_alone()
    {
        var client = Client("d");
        var meta = Path.Combine(client, "Assets/Generated/FixtureGame.cs.meta");
        File.WriteAllText(meta, "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(meta, stamp);
        File.AppendAllText(Path.Combine(client, "Assets/Generated/FixtureGame.cs"), "// stale\n");

        var result = await GenerateAsync(WriteConfig("config-d", Paths.Json(Paths.Protos), "../d"));

        result.ExitCode.ShouldBe(0, result.All);
        result.Output.ShouldContain("wrote Assets/Generated/FixtureGame.cs");
        File.ReadAllText(meta).ShouldBe("fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
        File.GetLastWriteTimeUtc(meta).ShouldBe(stamp);
    }

    [Fact]
    public async Task Verify_names_an_edited_output_and_writes_nothing()
    {
        var client = Client("e");
        var edited = Path.Combine(client, "Assets/Generated/FixtureSections.cs");
        File.AppendAllText(edited, "// edited by hand\n");
        var before = File.ReadAllBytes(edited);

        var result = await Tool.RunAsync("verify", "--config", WriteConfig("config-e", Paths.Json(Paths.Protos), "../e"), "--catalog", export.File);

        result.ExitCode.ShouldBe(2, result.All);
        result.Output.ShouldContain("differs: Assets/Generated/FixtureSections.cs");
        File.ReadAllBytes(edited).ShouldBe(before);
    }

    [Theory]
    [InlineData("output-root")]
    [InlineData("marker")]
    [InlineData("proto")]
    public async Task A_wrong_input_exits_1_naming_it_and_creates_no_folder(string wrong)
    {
        var client = Client("f");
        var config = WriteConfig("config-f", Paths.Json(Paths.Protos), "../f", proto: wrong == "proto" ? "missing_game.proto" : "fixture_game.proto");
        var extra = new List<string>();
        if (wrong == "output-root")
        {
            extra.AddRange(["--output-root", Path.Combine(_temp, "nowhere")]);
        }
        else if (wrong == "marker")
        {
            Directory.Move(Path.Combine(client, "Assets"), Path.Combine(client, "NotAssets"));
        }

        var before = Tree();

        var result = await Tool.RunAsync(["generate", "--config", config, "--catalog", export.File, .. extra]);

        result.ExitCode.ShouldBe(1, result.All);
        result.Error.ShouldContain(wrong switch
        {
            "output-root" => $"output root {Path.Combine(_temp, "nowhere")} does not exist",
            "marker" => $"marker {Path.Combine(client, "Assets")} does not exist under the output root",
            _ => "proto missing_game.proto does not exist under its root",
        });
        Tree().ShouldBe(before);
    }

    [Fact]
    public async Task A_game_proto_duplicating_a_framework_message_fails_before_any_write()
    {
        Client("g");
        var protos = Directory.CreateDirectory(Path.Combine(_temp, "protos")).FullName;
        File.Copy(Path.Combine(Paths.Protos, "fixture_game.proto"), Path.Combine(protos, "fixture_game.proto"));
        File.WriteAllText(Path.Combine(protos, "dup.proto"), "syntax = \"proto3\";\npackage gameserver.core.v1;\nmessage Section { int32 a = 1; }\n");
        var config = WriteConfig("config-g", Paths.Json(protos), "../g", extraProto: "dup.proto");
        var before = Tree();

        var result = await Tool.RunAsync("generate", "--config", config, "--catalog", export.File);

        result.ExitCode.ShouldBe(1, result.All);
        result.Error.ShouldContain("\"gameserver.core.v1.Section\" is already defined");
        Tree().ShouldBe(before);
    }

    [Fact]
    public async Task Cleaning_the_client_and_the_game_projects_leaves_the_generated_files()
    {
        // A copy of the fixture layout (the real projects stay built for the other tests); only the copies are built and cleaned.
        var root = Path.Combine(_temp, "fixture");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), File.ReadAllText(Path.Combine(Paths.Fixture, "Directory.Build.props"))
            .Replace("$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..'))", Paths.Framework + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"),
            $"<Project>\n  <Import Project=\"{Path.Combine(Paths.Framework, "Directory.Packages.props")}\" />\n</Project>\n");
        Copy(Path.Combine(Paths.Fixture, "client"), Path.Combine(root, "client"));
        Copy(Path.Combine(Paths.Fixture, "src", "Fixture.Game"), Path.Combine(root, "src", "Fixture.Game"));
        var generated = Path.Combine(root, "client", "Assets", "Generated");
        var before = Snapshot(generated);

        foreach (var project in new[] { "client/Fixture.Client.csproj", "src/Fixture.Game/Fixture.Game.csproj" })
        {
            var path = Path.Combine(root, project);
            var build = await Child.RunAsync(Paths.Dotnet, ["build", path, "-c", Paths.Configuration, "-p:BuildProjectReferences=false"], root, TimeSpan.FromMinutes(5));
            build.ExitCode.ShouldBe(0, build.All);
            var clean = await Child.RunAsync(Paths.Dotnet, ["clean", path, "-c", Paths.Configuration, "-p:BuildProjectReferences=false"], root, TimeSpan.FromMinutes(5));
            clean.ExitCode.ShouldBe(0, clean.All);
        }

        Snapshot(generated).ShouldBe(before);
    }

    /// <summary>No BOM, no absolute path, user, machine or timestamp (FR-023).</summary>
    private void CheckClean(string output, byte[] bytes)
    {
        bytes.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()).ShouldBeFalse($"{output}: BOM");
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var forbidden in new[] { _temp, Paths.Framework, Paths.Framework.Replace('\\', '/'), Environment.UserName, Environment.MachineName })
        {
            text.ShouldNotContain(forbidden, Case.Insensitive, $"{output} contains '{forbidden}'");
        }

        DateLike().IsMatch(text).ShouldBeFalse($"{output} contains a date or time");
    }

    private Task<ChildResult> GenerateAsync(string config) => Tool.RunAsync("generate", "--config", config, "--catalog", export.File);

    /// <summary>A copy of the committed client (its marker and generated files) at <c>{temp}/{name}</c>.</summary>
    private string Client(string name)
    {
        var client = Path.Combine(_temp, name);
        Copy(Path.Combine(Paths.Fixture, "client", "Assets"), Path.Combine(client, "Assets"));
        return client;
    }

    private string WriteConfig(
        string folder,
        string protosRoot,
        string outputRoot,
        string lineEndings = "lf",
        string proto = "fixture_game.proto",
        string? extraProto = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_temp, folder)).FullName;
        var files = extraProto is null ? $"\"{proto}\"" : $"\"{proto}\", \"{extraProto}\"";
        var path = Path.Combine(directory, "wiregen.json");
        File.WriteAllText(path, $$"""
            {
              "format": "gameserver.wiregen/1",
              "catalog": { "file": "{{Paths.Json(export.File)}}" },
              "protos": [ { "root": "{{protosRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "files": [{{files}}] } ],
              "output": { "root": "{{outputRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "marker": "Assets", "directory": "Assets/Generated", "lineEndings": "{{lineEndings}}" },
              "namespace": "Fixture.Client",
              "routesClass": "FixtureRoutes",
              "sectionsClass": "FixtureSections"
            }
            """);
        return path;
    }

    /// <summary>The path with every other separator flipped (FR-024).</summary>
    private static string Mixed(string path)
    {
        var flip = false;
        return string.Concat(path.Select(c =>
        {
            if (c is not ('/' or '\\'))
            {
                return c;
            }

            flip = !flip;
            return flip ? '/' : '\\';
        }));
    }

    private string Tree() => Snapshot(_temp);

    private static string Snapshot(string folder) => string.Join(
        "\n",
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(e => File.Exists(e) ? $"{e} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e)))} {File.GetLastWriteTimeUtc(e).Ticks}" : e));

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj"))
            {
                continue;
            }

            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    [GeneratedRegex(@"\b(19|20)\d\d-\d\d-\d\d\b|\b\d\d:\d\d:\d\d\b")]
    private static partial Regex DateLike();

    /// <summary>The fixture host's catalog export, made once per class through the host's own export mode.</summary>
    public sealed class Export : IAsyncLifetime
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("fixture-export-").FullName;

        public string File => Path.Combine(_folder, "wire-contract.json");

        public async ValueTask InitializeAsync()
        {
            var result = await Child.RunAsync(
                Paths.Dotnet,
                ["run", "--project", Paths.Host, "--no-launch-profile", "--no-build", "-c", Paths.Configuration, "--", "--environment", "Local", "--export-wire-contract", File],
                Path.GetDirectoryName(Paths.Host)!,
                TimeSpan.FromMinutes(5));
            result.ExitCode.ShouldBe(0, result.All);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_folder, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The tool, as the game's documented command runs it (built output of the solution build).</summary>
    private static class Tool
    {
        public static Task<ChildResult> RunAsync(params string[] args)
        {
            File.Exists(Paths.ToolDll).ShouldBeTrue($"{Paths.ToolDll} is missing. Build the solution ({Paths.Configuration}) first.");
            return Child.RunAsync(Paths.Dotnet, [Paths.ToolDll, .. args], Paths.Fixture, TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>Repository paths of the fixture and its framework checkout.</summary>
    private static class Paths
    {
        public static string Fixture { get; } = FindFixture();

        public static string Framework { get; } = Path.GetFullPath(Path.Combine(Fixture, "..", ".."));

        public static string Config => Path.Combine(Fixture, "tools", "wiregen.json");

        public static string Protos => Path.Combine(Fixture, "src", "Fixture.Game", "Protos");

        public static string Host => Path.Combine(Fixture, "src", "Fixture.Host", "Fixture.Host.csproj");

        public static string ClientGenerated => Path.Combine(Fixture, "client", "Assets", "Generated");

        /// <summary>This test's build configuration (<c>bin/&lt;configuration&gt;/&lt;tfm&gt;/</c>): the host and the tool run with the same.</summary>
        public static string Configuration { get; } = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;

        public static string ToolDll => Path.Combine(Framework, "tools", "GameServer.WireGen", "bin", Configuration, "net10.0", "GameServer.WireGen.dll");

        /// <summary>V15: DOTNET_HOST_PATH and DOTNET_ROOT are not set under dotnet test; the resolved path is passed explicitly.</summary>
        public static string Dotnet { get; } = ResolveDotnet();

        public static string Json(string path) => path.Replace('\\', '/');

        private static string ResolveDotnet()
        {
            var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && System.IO.File.Exists(host))
            {
                return host;
            }

            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && System.IO.File.Exists(Path.Combine(root, exe)))
            {
                return Path.Combine(root, exe);
            }

            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder.Trim('"'), exe))
                .FirstOrDefault(System.IO.File.Exists) ?? throw new InvalidOperationException("dotnet not found (DOTNET_HOST_PATH, DOTNET_ROOT, PATH).");
        }

        private static string FindFixture()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (System.IO.File.Exists(Path.Combine(directory.FullName, "tools", "wiregen.json")) && Directory.Exists(Path.Combine(directory.FullName, "client")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("The consumer fixture (tools/wiregen.json) was not found above " + AppContext.BaseDirectory);
        }
    }
}

/// <summary>Exit code and captured output of one child process.</summary>
public sealed record ChildResult(int ExitCode, string Output, string Error)
{
    public string All => Output + Error;
}

/// <summary>Runs a child process to completion; MSBuild variables of the calling <c>dotnet test</c> are removed.</summary>
internal static class Child
{
    public static async Task<ChildResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', start.ArgumentList)} did not finish within {timeout}.");
        }

        return new ChildResult(process.ExitCode, await output, await error);
    }
}
