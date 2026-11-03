using GameServer.LocalRun;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T055 (FR-042..FR-045): the host resolver of <c>run-local</c> on temporary host layouts — project file or folder,
/// launch profile port and flavor, settings layering, governing <c>global.json</c>, and one message per invalid input.
/// </summary>
public sealed class LocalRunDescribeTests : IDisposable
{
    private const string Profile = "local";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-local-" + Guid.NewGuid().ToString("N"));

    private string HostDir => Path.Combine(_root, "repo", "src", "Game.Host");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_project_file_and_its_folder_describe_the_same_host()
    {
        WriteHost();

        var fromFile = Describe(Path.Combine(HostDir, "Game.Host.csproj"));
        var fromFolder = Describe(HostDir);

        fromFolder.ShouldBe(fromFile);
        fromFile.Project.ShouldBe(Slashed(Path.Combine(HostDir, "Game.Host.csproj")));
        fromFile.ProjectDir.ShouldBe(Slashed(HostDir));
        fromFile.ProjectName.ShouldBe("Game.Host");
        fromFile.IsSample.ShouldBeFalse();
        fromFile.Flavor.ShouldBe("Local");
        fromFile.Port.ShouldBe(5180);
        fromFile.PortNote.ShouldBeEmpty();
        fromFile.SdkVersion.ShouldBe("10.0.403");
        fromFile.GlobalJson.ShouldBe(Slashed(Path.Combine(_root, "repo", "global.json")));
    }

    [Fact]
    public void Relative_host_paths_resolve_against_the_current_directory()
    {
        WriteHost();

        var description = LocalHostDescription.Describe("src/Game.Host", Profile, Path.Combine(_root, "repo"), RepositoryTooling.Root, []);

        description.ProjectDir.ShouldBe(Slashed(HostDir));
    }

    [Fact]
    public void A_folder_needs_exactly_one_project()
    {
        WriteHost();
        var empty = Directory.CreateDirectory(Path.Combine(_root, "repo", "empty")).FullName;
        File.WriteAllText(Path.Combine(HostDir, "Second.csproj"), "<Project />");

        Fails(empty).ShouldContain("no *.csproj in " + Slashed(empty));
        Fails(HostDir).ShouldContain("2 projects in " + Slashed(HostDir) + " (Game.Host.csproj, Second.csproj)");
        Fails(Path.Combine(_root, "missing")).ShouldContain("not found");
        Fails(Path.Combine(HostDir, "appsettings.json")).ShouldContain("is not a *.csproj file");
    }

    [Theory]
    [InlineData("http://localhost:5181", 5181)]
    [InlineData("https://localhost:7001;http://localhost:5182;http://localhost:5183", 5182)]
    [InlineData("HTTP://0.0.0.0:5184", 5184)]
    public void The_port_is_the_first_http_url_of_the_profile(string urls, int port)
    {
        WriteHost(applicationUrl: urls);

        var description = Describe(HostDir);

        description.Port.ShouldBe(port);
        description.PortNote.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://localhost:7001")]
    [InlineData(null)]
    public void A_profile_without_an_http_url_uses_5080_with_a_note(string? urls)
    {
        WriteHost(applicationUrl: urls);

        var description = Describe(HostDir);

        description.Port.ShouldBe(LocalHostDescription.DefaultPort);
        description.PortNote.ShouldBe("no http URL in profile 'local'; using 5080");
    }

    [Fact]
    public void Launch_settings_may_have_comments_and_trailing_commas()
    {
        WriteHost(launchSettings: """
            {
              // developer profiles
              "profiles": {
                "local": {
                  /* the local flavor */
                  "applicationUrl": "http://localhost:5190",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local", },
                },
              },
            }
            """);

        Describe(HostDir).Port.ShouldBe(5190);
    }

    [Fact]
    public void Folders_come_from_the_flavor_settings_over_the_base_file_and_environment_variables_override_both()
    {
        WriteHost(
            baseSettings: """{ "FileStore": { "Folder": "base-data" }, "Observability": { "Logging": { "Directory": "base-logs" } } }""",
            flavorSettings: """{ "FileStore": { "Folder": "../../.local/data" } }""");

        var layered = Describe(HostDir);
        layered.DataFolder.ShouldBe(Slashed(Path.GetFullPath(Path.Combine(HostDir, "..", "..", ".local", "data"))));
        layered.DataFolderSetting.ShouldBe("../../.local/data");
        layered.LogFolder.ShouldBe(Slashed(Path.Combine(HostDir, "base-logs")));

        var overridden = Describe(HostDir, [KeyValuePair.Create("FileStore__Folder", (string?)Path.Combine(_root, "env-data"))]);
        overridden.DataFolder.ShouldBe(Slashed(Path.Combine(_root, "env-data")));
    }

    [Fact]
    public void Profile_variables_override_inherited_ones_and_defaults_apply_when_unset()
    {
        WriteHost(profileVariables: """, "FileStore__Folder": "profile-data" """);

        var description = Describe(HostDir, [KeyValuePair.Create("FileStore__Folder", (string?)"env-data")]);
        description.DataFolder.ShouldBe(Slashed(Path.Combine(HostDir, "profile-data")));
        description.LogFolder.ShouldBe(Slashed(Path.Combine(HostDir, "logs")));

        WriteHost();
        var unset = Describe(HostDir);
        unset.DataFolder.ShouldBeEmpty();
        unset.DataFolderSetting.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_launch_settings_file_unknown_profile_or_missing_flavor_is_refused()
    {
        WriteHost();
        Fails(HostDir, "staging").ShouldBe(
            $"launch profile 'staging' not found in {Slashed(Path.Combine(HostDir, "Properties", "launchSettings.json"))} (defined: local, other). " +
            "Fix: pass one of the defined profiles.");

        WriteHost(launchSettings: """{ "profiles": { "local": { "applicationUrl": "http://localhost:5180" } } }""");
        Fails(HostDir).ShouldContain("sets no environmentVariables.ASPNETCORE_ENVIRONMENT");

        File.Delete(Path.Combine(HostDir, "Properties", "launchSettings.json"));
        Fails(HostDir).ShouldStartWith("no Properties/launchSettings.json in " + Slashed(HostDir));
    }

    [Fact]
    public void Broken_json_names_the_file_and_the_position()
    {
        WriteHost(launchSettings: "{\n  \"profiles\": {\n    \"local\": { oops }\n  }\n}");
        Fails(HostDir).ShouldBe($"unreadable JSON in {Slashed(Path.Combine(HostDir, "Properties", "launchSettings.json"))} at line 3, position 16. Fix: correct the file.");

        WriteHost(flavorSettings: "{\n  \"FileStore\": { \"Folder\": \"data\" ]\n}");
        Fails(HostDir).ShouldBe($"unreadable JSON in {Slashed(Path.Combine(HostDir, "appsettings.Local.json"))} at line 2, position 35. Fix: correct the file.");
    }

    [Fact]
    public void The_nearest_global_json_above_the_project_governs()
    {
        WriteHost();
        File.WriteAllText(Path.Combine(HostDir, "..", "global.json"), """{ "sdk": { "version": "10.0.999" } }""");

        var description = Describe(HostDir);

        description.SdkVersion.ShouldBe("10.0.999");
        description.GlobalJson.ShouldBe(Slashed(Path.GetFullPath(Path.Combine(HostDir, "..", "global.json"))));
    }

    [Theory]
    [InlineData("10", "(10)")]
    [InlineData("{ \"major\": 10 }", "({ \"major\": 10 })")]
    [InlineData("\"\"", "(\"\")")]
    public void A_global_json_whose_sdk_version_is_not_a_string_is_invalid_host_input(string version, string shown)
    {
        WriteHost();
        var globalJson = Path.Combine(_root, "repo", "global.json");
        File.WriteAllText(globalJson, $$"""{ "sdk": { "version": {{version}} } }""");

        Fails(HostDir).ShouldBe(
            $"{Slashed(globalJson)} has an sdk.version that is not a version string {shown}. Fix: write it as a quoted string (\"sdk\": {{ \"version\": \"10.0.403\" }}).");
    }

    [Fact]
    public void A_host_without_a_global_json_above_it_is_refused()
    {
        Assert.SkipWhen(
            HasGlobalJsonAbove(Path.GetTempPath()),
            "A global.json above the temp folder governs every temp host; the missing-global.json case cannot be built here.");
        WriteHost();
        File.Delete(Path.Combine(_root, "repo", "global.json"));

        Fails(HostDir).ShouldStartWith($"no global.json in {Slashed(HostDir)} or above.");

        File.WriteAllText(Path.Combine(_root, "repo", "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Fails(HostDir).ShouldStartWith($"{Slashed(Path.Combine(_root, "repo", "global.json"))} has no sdk.version.");
    }

    [Fact]
    public void The_framework_sample_host_is_the_011_default()
    {
        var description = LocalHostDescription.Describe(
            Path.Combine(RepositoryTooling.Root, "samples", "SampleHost"), Profile, RepositoryTooling.Root, RepositoryTooling.Root, []);

        description.IsSample.ShouldBeTrue();
        description.Flavor.ShouldBe("Local");
        description.Port.ShouldBe(5080);
        description.DataFolderSetting.ShouldBe("../_database/dbbase");
        description.LogFolder.ShouldBe(Slashed(Path.Combine(RepositoryTooling.Root, "samples", "SampleHost", "logs")));
        description.SdkVersion.ShouldBe("10.0.403");
    }

    [Fact]
    public async Task The_tool_prints_key_value_lines_and_exits_2_with_one_message_on_invalid_input()
    {
        var dotnet = RepositoryTooling.Dotnet();
        Assert.SkipWhen(dotnet is null, "No dotnet executable (DOTNET_HOST_PATH, DOTNET_ROOT, PATH).");
        WriteHost();
        var tool = Path.Combine(AppContext.BaseDirectory, "GameServer.LocalRun.dll");

        var ok = await RepositoryTooling.RunAsync(dotnet!, [tool, "describe", "--host-project", HostDir, "--launch-profile", Profile], _root, ToolTimeout);
        ok.ExitCode.ShouldBe(0, ok.All);
        ok.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('=')[0]).ShouldBe(
            ["project", "projectDir", "projectName", "isSample", "flavor", "port", "portNote", "dataFolder", "dataFolderSetting", "logFolder", "sdkVersion", "globalJson"]);
        ok.Output.ShouldContain("port=5180");

        var unknown = await RepositoryTooling.RunAsync(dotnet!, [tool, "describe", "--host-project", HostDir, "--launch-profile", "nope"], _root, ToolTimeout);
        unknown.ExitCode.ShouldBe(2);
        unknown.Output.ShouldBeEmpty();
        unknown.Error.Trim().ShouldStartWith("launch profile 'nope' not found");

        var usage = await RepositoryTooling.RunAsync(dotnet!, [tool, "describe", "--bogus", "x"], _root, ToolTimeout);
        usage.ExitCode.ShouldBe(2);
        usage.Error.ShouldContain("unknown option '--bogus'");
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    private static bool HasGlobalJsonAbove(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return true;
            }
        }

        return false;
    }

    private LocalHostDescription Describe(string hostProject, IEnumerable<KeyValuePair<string, string?>>? environment = null) =>
        LocalHostDescription.Describe(hostProject, Profile, _root, RepositoryTooling.Root, environment ?? []);

    private string Fails(string hostProject, string profile = Profile) =>
        Should.Throw<LocalRunException>(() => LocalHostDescription.Describe(hostProject, profile, _root, RepositoryTooling.Root, [])).Message;

    /// <summary>A consumer-shaped host: <c>repo/global.json</c>, <c>repo/src/Game.Host/{Game.Host.csproj, Properties/launchSettings.json, appsettings*.json}</c>.</summary>
    private void WriteHost(
        string? applicationUrl = "http://localhost:5180",
        string? launchSettings = null,
        string profileVariables = "",
        string baseSettings = "{}",
        string flavorSettings = "{}")
    {
        Directory.CreateDirectory(Path.Combine(HostDir, "Properties"));
        File.WriteAllText(Path.Combine(_root, "repo", "global.json"), """{ "sdk": { "version": "10.0.403", "rollForward": "latestPatch" } }""");
        File.WriteAllText(Path.Combine(HostDir, "Game.Host.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
        var url = applicationUrl is null ? string.Empty : $"\"applicationUrl\": \"{applicationUrl}\",";
        File.WriteAllText(
            Path.Combine(HostDir, "Properties", "launchSettings.json"),
            launchSettings ?? $$"""
                {
                  "profiles": {
                    "local": { {{url}} "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local" {{profileVariables}} } },
                    "other": { "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Dev" } }
                  }
                }
                """);
        File.WriteAllText(Path.Combine(HostDir, "appsettings.json"), baseSettings);
        File.WriteAllText(Path.Combine(HostDir, "appsettings.Local.json"), flavorSettings);
    }
}
