using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GameServer.LocalRun;

/// <summary>
/// What <c>run-local</c> needs from one host project (data-model §7, contracts/run-local-cli.md §2): the project, the
/// flavor and default port of a launch profile, the data and log folders of that flavor's settings and the governing
/// <c>global.json</c>. Paths are absolute with <c>/</c> separators.
/// </summary>
public sealed record LocalHostDescription(
    string Project,
    string ProjectDir,
    string ProjectName,
    bool IsSample,
    string Flavor,
    int Port,
    string PortNote,
    string DataFolder,
    string DataFolderSetting,
    string LogFolder,
    string SdkVersion,
    string GlobalJson)
{
    public const int DefaultPort = 5080;
    public const string SampleProject = "samples/SampleHost/SampleHost.csproj";
    public const string DefaultLogDirectory = "logs";

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The <c>key=value</c> lines printed by <c>describe</c>, in data-model §7 order.</summary>
    public IReadOnlyList<string> ToLines() =>
    [
        $"project={Project}",
        $"projectDir={ProjectDir}",
        $"projectName={ProjectName}",
        $"isSample={(IsSample ? "true" : "false")}",
        $"flavor={Flavor}",
        $"port={Port}",
        $"portNote={PortNote}",
        $"dataFolder={DataFolder}",
        $"dataFolderSetting={DataFolderSetting}",
        $"logFolder={LogFolder}",
        $"sdkVersion={SdkVersion}",
        $"globalJson={GlobalJson}",
    ];

    /// <summary>
    /// Resolves <paramref name="hostProject"/> (project file, or a folder with exactly one <c>*.csproj</c>; relative to
    /// <paramref name="currentDirectory"/>). <paramref name="environment"/> are the variables the host would inherit; the
    /// profile's own <c>environmentVariables</c> override them, as <c>dotnet run --launch-profile</c> does.
    /// </summary>
    /// <exception cref="LocalRunException">An input is missing or invalid; the message names the problem and the fix.</exception>
    public static LocalHostDescription Describe(
        string hostProject,
        string launchProfile,
        string currentDirectory,
        string frameworkRoot,
        IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var project = ResolveProject(hostProject, currentDirectory);
        var projectDir = Path.GetDirectoryName(project)!;
        var profile = ReadProfile(projectDir, launchProfile, out var launchSettings);

        var profileVariables = profile.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object
            ? variables.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText())
            : [];
        if (!profileVariables.TryGetValue("ASPNETCORE_ENVIRONMENT", out var flavor) || string.IsNullOrWhiteSpace(flavor))
        {
            throw new LocalRunException(
                $"launch profile '{launchProfile}' in {Slashed(launchSettings)} sets no environmentVariables.ASPNETCORE_ENVIRONMENT. " +
                "Fix: add it (the flavor, for example \"Local\").");
        }

        var (port, portNote) = ReadPort(profile, launchProfile);
        var settings = ReadSettings(projectDir, flavor, environment, profileVariables);
        var dataSetting = settings["FileStore:Folder"] ?? string.Empty;
        var logSetting = settings["Observability:Logging:Directory"] is { Length: > 0 } logs ? logs : DefaultLogDirectory;
        var (sdkVersion, globalJson) = ReadSdk(projectDir);

        return new LocalHostDescription(
            Slashed(project),
            Slashed(projectDir),
            Path.GetFileNameWithoutExtension(project),
            string.Equals(project, Path.GetFullPath(Path.Combine(frameworkRoot, SampleProject)), PathComparison),
            flavor,
            port,
            portNote,
            dataSetting.Length == 0 ? string.Empty : Slashed(Path.GetFullPath(dataSetting, projectDir)),
            dataSetting,
            Slashed(Path.GetFullPath(logSetting, projectDir)),
            sdkVersion,
            Slashed(globalJson));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string ResolveProject(string hostProject, string currentDirectory)
    {
        var path = Path.GetFullPath(hostProject, currentDirectory);
        if (File.Exists(path))
        {
            return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? path
                : throw new LocalRunException($"host project {Slashed(path)} is not a *.csproj file. Fix: pass the host's project file or its folder.");
        }

        if (!Directory.Exists(path))
        {
            throw new LocalRunException($"host project {Slashed(path)} not found. Fix: pass the host's project file or its folder (relative to the current directory).");
        }

        var projects = Directory.GetFiles(path, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new LocalRunException($"no *.csproj in {Slashed(path)}. Fix: pass the host's project file or the folder that contains it."),
            _ => throw new LocalRunException(
                $"{projects.Length} projects in {Slashed(path)} ({string.Join(", ", projects.Select(Path.GetFileName).Order(StringComparer.Ordinal))}). " +
                "Fix: pass the host's project file."),
        };
    }

    private static JsonElement ReadProfile(string projectDir, string launchProfile, out string file)
    {
        file = Path.Combine(projectDir, "Properties", "launchSettings.json");
        if (!File.Exists(file))
        {
            throw new LocalRunException(
                $"no Properties/launchSettings.json in {Slashed(projectDir)}. Fix: add a launch profile with applicationUrl and " +
                "environmentVariables.ASPNETCORE_ENVIRONMENT (see the framework's samples/SampleHost/Properties/launchSettings.json).");
        }

        var root = ParseJson(file);
        var profiles = root.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Object ? p.EnumerateObject().ToArray() : [];
        var match = profiles.FirstOrDefault(candidate => candidate.Name == launchProfile);
        if (match.Value.ValueKind != JsonValueKind.Object)
        {
            var defined = profiles.Length == 0 ? "none" : string.Join(", ", profiles.Select(candidate => candidate.Name));
            throw new LocalRunException($"launch profile '{launchProfile}' not found in {Slashed(file)} (defined: {defined}). Fix: pass one of the defined profiles.");
        }

        return match.Value;
    }

    private static (int Port, string Note) ReadPort(JsonElement profile, string launchProfile)
    {
        var urls = profile.TryGetProperty("applicationUrl", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return (uri.Port, string.Empty);
            }
        }

        return (DefaultPort, $"no http URL in profile '{launchProfile}'; using {DefaultPort}");
    }

    private static IConfiguration ReadSettings(
        string projectDir,
        string flavor,
        IEnumerable<KeyValuePair<string, string?>> environment,
        Dictionary<string, string?> profileVariables)
    {
        string[] files = ["appsettings.json", $"appsettings.{flavor}.json"];
        foreach (var file in files.Select(name => Path.Combine(projectDir, name)).Where(File.Exists))
        {
            ParseJson(file);
        }

        // Same layering as WebApplication.CreateBuilder: base file, flavor file, environment variables ("__" = ":").
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environment.Concat(profileVariables))
        {
            variables[name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)] = value;
        }

        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(projectDir)
                .AddJsonFile(files[0], optional: true, reloadOnChange: false)
                .AddJsonFile(files[1], optional: true, reloadOnChange: false)
                .AddInMemoryCollection(variables)
                .Build();
        }
        catch (InvalidDataException exception)
        {
            throw new LocalRunException($"unreadable host settings: {exception.Message} {exception.InnerException?.Message} Fix: correct the JSON.");
        }
    }

    private static (string Version, string File) ReadSdk(string projectDir)
    {
        for (var directory = new DirectoryInfo(projectDir); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(file))
            {
                continue;
            }

            var root = ParseJson(file);
            return root.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object
                && sdk.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } text
                ? (text, file)
                : throw new LocalRunException($"{Slashed(file)} has no sdk.version. Fix: pin the .NET SDK there (\"sdk\": {{ \"version\": \"...\" }}).");
        }

        throw new LocalRunException(
            $"no global.json in {Slashed(projectDir)} or above. Fix: add one with sdk.version at the repository root (as the framework's global.json).");
    }

    private static JsonElement ParseJson(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new LocalRunException(
                $"unreadable JSON in {Slashed(file)} at line {exception.LineNumber + 1}, position {exception.BytePositionInLine + 1}. Fix: correct the file.");
        }
    }

    private static string Slashed(string path) => path.Replace('\\', '/');
}

/// <summary>Invalid host input: the message names the problem and the fix (exit code 2).</summary>
public sealed class LocalRunException(string message) : Exception(message);
