using System.Text.Json;

namespace GameServer.WireGen;

/// <summary>How proto C# files are placed in <c>output.directory</c>.</summary>
internal enum OutputLayout
{
    /// <summary>File name only.</summary>
    Flat,

    /// <summary>The proto's folder under its root (<c>gameserver/core/v1/Core.cs</c>).</summary>
    ByRoot,
}

/// <summary>Line ending of every generated file.</summary>
internal enum LineEnding
{
    Lf,
    Crlf,
}

/// <summary>A protoc import root and the protos listed under it (paths relative to the root, <c>/</c>-separated).</summary>
internal sealed record ProtoSet(string Root, IReadOnlyList<string> Files);

/// <summary>
/// The generator configuration <c>wiregen.json</c>, format <c>gameserver.wiregen/1</c> (data-model §4, contracts/wiregen-cli.md §4
/// steps 1–3): every path resolved relative to the folder of the file with <see cref="Path.GetFullPath(string)"/> (mixed
/// <c>/</c> and <c>\</c> accepted). <see cref="Load"/> checks that the output root, the marker, the output directory and every
/// proto exist and creates nothing (FR-025, N8).
/// </summary>
internal sealed record WireGenConfig(
    string ConfigFile,
    string? HostProject,
    string Environment,
    string? CatalogFile,
    string Origin,
    IReadOnlyList<ProtoSet> Protos,
    IReadOnlyList<string> ImportRoots,
    string OutputRoot,
    string OutputDirectory,
    OutputLayout Layout,
    LineEnding LineEndings,
    string Namespace,
    string RoutesClass,
    string SectionsClass)
{
    public const string Format = "gameserver.wiregen/1";

    private const string FormatPrefix = "gameserver.wiregen/";

    /// <summary>
    /// Reads and checks <paramref name="configFile"/>. <paramref name="outputRoot"/> (<c>--output-root</c>) replaces
    /// <c>output.root</c> (resolved against the current directory); <paramref name="catalogFile"/> (<c>--catalog</c>) makes the
    /// <c>catalog</c> section optional. Throws <see cref="WireGenException"/> with one line per problem.
    /// </summary>
    public static WireGenConfig Load(string configFile, string? outputRoot = null, string? catalogFile = null)
    {
        var file = Path.GetFullPath(Normalize(configFile));
        if (!File.Exists(file))
        {
            throw new WireGenException($"[config] {file} does not exist. Fix: pass --config <path of wiregen.json>.");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new WireGenException($"[config] {file} is not valid JSON: {ex.Message} Fix: correct the file (format {Format}).");
        }

        var reader = new Reader(file, Path.GetDirectoryName(file)!);
        var config = reader.Read(root, outputRoot, catalogFile);
        reader.ThrowIfAny();
        CheckPaths(config, catalogFile is null);
        return config;
    }

    /// <summary>The full path without a trailing separator (<c>../client/</c> and <c>..\client</c> are one folder).</summary>
    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Accepts <c>/</c> and <c>\</c> on every OS (Windows takes both; elsewhere <c>\</c> would be a file name character).</summary>
    internal static string Normalize(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/');

    /// <summary>Steps 2–3 of contracts/wiregen-cli.md §4: nothing is created when anything is missing.</summary>
    private static void CheckPaths(WireGenConfig config, bool needsCatalog)
    {
        var errors = new List<string>();
        if (!Directory.Exists(config.OutputRoot))
        {
            errors.Add($"[config] output root {config.OutputRoot} does not exist (output.root in {config.ConfigFile}). Fix: check out the client there " +
                "or pass --output-root <client root>.");
        }
        else
        {
            if (config.Marker is { } marker && !File.Exists(marker) && !Directory.Exists(marker))
            {
                errors.Add($"[config] marker {marker} does not exist under the output root {config.OutputRoot} (output.marker). Fix: pass the client " +
                    "root with --output-root <client root>, or correct output.marker.");
            }

            if (!Directory.Exists(config.OutputDirectory))
            {
                errors.Add($"[config] output directory {config.OutputDirectory} does not exist (output.directory, never created). Fix: create it in " +
                    "the client, or pass the client root with --output-root <client root>.");
            }
        }

        foreach (var set in config.Protos)
        {
            if (!Directory.Exists(set.Root))
            {
                errors.Add($"[config] proto root {set.Root} does not exist (protos[].root in {config.ConfigFile}). Fix: correct the root.");
                continue;
            }

            foreach (var proto in set.Files.Where(p => !File.Exists(Path.Combine(set.Root, p))))
            {
                errors.Add($"[config] proto {proto} does not exist under its root {set.Root}. Fix: correct protos[].files or protos[].root.");
            }
        }

        foreach (var importRoot in config.ImportRoots.Where(r => !Directory.Exists(r)))
        {
            errors.Add($"[config] import root {importRoot} does not exist (importRoots in {config.ConfigFile}). Fix: correct the path.");
        }

        if (needsCatalog && config.HostProject is { } host && !File.Exists(host) && !Directory.Exists(host))
        {
            errors.Add($"[config] host project {host} does not exist (catalog.hostProject). Fix: correct the path of the host .csproj.");
        }

        if (needsCatalog && config.CatalogFile is { } catalog && !File.Exists(catalog))
        {
            errors.Add($"[config] catalog file {catalog} does not exist (catalog.file). Fix: correct the path or export it with --export-wire-contract.");
        }

        if (errors.Count > 0)
        {
            throw new WireGenException(errors);
        }
    }

    /// <summary><c>output.marker</c> resolved under the output root; null when the config has none.</summary>
    public string? Marker { get; init; }

    /// <summary>Reads the fields of data-model §4, collecting one line per bad field.</summary>
    private sealed class Reader(string file, string folder)
    {
        private readonly List<string> _errors = [];

        public WireGenConfig Read(JsonElement root, string? outputRootOverride, string? catalogOverride)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WireGenException($"[config] {file}: the file must hold one JSON object (format {Format}).");
            }

            var format = String(root, "format", required: true);
            if (format is not null && format != Format)
            {
                _errors.Add(format.StartsWith(FormatPrefix, StringComparison.Ordinal)
                    ? $"[config] {file}: format '{format}' is a major version this generator does not read (it reads {Format}). Fix: use a matching framework checkout."
                    : $"[config] {file}: format '{format}' is not a WireGen configuration. Fix: set \"format\": \"{Format}\".");
            }

            string? hostProject = null;
            string? catalogFile = null;
            var environment = "Local";
            if (root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Object)
            {
                hostProject = Path(catalog, "hostProject", "catalog.hostProject", required: false);
                catalogFile = Path(catalog, "file", "catalog.file", required: false);
                environment = String(catalog, "environment", required: false, field: "catalog.environment") ?? environment;
            }
            else if (root.TryGetProperty("catalog", out var wrong) && wrong.ValueKind != JsonValueKind.Null)
            {
                _errors.Add($"[config] {file}: catalog must be an object {{ \"hostProject\": … }} or {{ \"file\": … }}.");
            }

            if (catalogOverride is null && (hostProject is null) == (catalogFile is null))
            {
                _errors.Add($"[config] {file}: set exactly one of catalog.hostProject (the host .csproj, exported with --export-wire-contract) and " +
                    "catalog.file (a committed export), or pass --catalog <export file>.");
            }

            var origin = String(root, "origin", required: false) ?? "game";
            if (origin is not ("game" or "framework"))
            {
                _errors.Add($"[config] {file}: origin '{origin}' must be 'game' or 'framework'.");
            }

            var protos = Protos(root);
            var importRoots = Strings(root, "importRoots").Select(Resolve).ToArray();
            var output = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            if (output.ValueKind != JsonValueKind.Object)
            {
                _errors.Add($"[config] {file}: output is required: {{ \"directory\": \"<folder under output.root>\" }}.");
            }

            var outputRoot = outputRootOverride is not null
                ? Full(Normalize(outputRootOverride))
                : (output.ValueKind == JsonValueKind.Object ? Path(output, "root", "output.root", required: false) : null) ?? folder;
            string? marker = null;
            var directory = outputRoot;
            var layout = OutputLayout.Flat;
            var lineEndings = LineEnding.Lf;
            if (output.ValueKind == JsonValueKind.Object)
            {
                marker = Under(outputRoot, String(output, "marker", required: false, field: "output.marker"));
                directory = Under(outputRoot, String(output, "directory", required: true, field: "output.directory")) ?? outputRoot;
                layout = String(output, "layout", required: false, field: "output.layout") switch
                {
                    null or "flat" => OutputLayout.Flat,
                    "by-root" => OutputLayout.ByRoot,
                    var other => Invalid(OutputLayout.Flat, $"output.layout '{other}' must be 'flat' or 'by-root'."),
                };
                lineEndings = String(output, "lineEndings", required: false, field: "output.lineEndings") switch
                {
                    null or "lf" => LineEnding.Lf,
                    "crlf" => LineEnding.Crlf,
                    var other => Invalid(LineEnding.Lf, $"output.lineEndings '{other}' must be 'lf' or 'crlf'."),
                };
            }

            var ns = String(root, "namespace", required: true) ?? string.Empty;
            if (ns.Length > 0 && !ns.Split('.').All(Names.IsIdentifier))
            {
                _errors.Add($"[config] {file}: namespace '{ns}' is not a C# namespace (identifiers joined by '.').");
            }

            var routesClass = ClassName(root, "routesClass", "GameRoutes");
            var sectionsClass = ClassName(root, "sectionsClass", "GameSections");
            if (routesClass == sectionsClass)
            {
                _errors.Add($"[config] {file}: routesClass and sectionsClass are both '{routesClass}'. Fix: give them different names.");
            }

            return new WireGenConfig(
                file,
                hostProject,
                environment,
                catalogOverride is not null ? Full(Normalize(catalogOverride)) : catalogFile,
                origin,
                protos,
                importRoots,
                outputRoot,
                directory,
                layout,
                lineEndings,
                ns,
                routesClass,
                sectionsClass)
            {
                Marker = marker,
            };
        }

        public void ThrowIfAny()
        {
            if (_errors.Count > 0)
            {
                throw new WireGenException(_errors);
            }
        }

        private T Invalid<T>(T fallback, string message)
        {
            _errors.Add($"[config] {file}: {message}");
            return fallback;
        }

        private ProtoSet[] Protos(JsonElement root)
        {
            if (!root.TryGetProperty("protos", out var protos) || protos.ValueKind != JsonValueKind.Array || protos.GetArrayLength() == 0)
            {
                _errors.Add($"[config] {file}: protos is required: [{{ \"root\": \"<import root>\", \"files\": [\"<proto relative to root>\"] }}].");
                return [];
            }

            var sets = new List<ProtoSet>();
            var index = 0;
            foreach (var entry in protos.EnumerateArray())
            {
                var field = $"protos[{index++}]";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    _errors.Add($"[config] {file}: {field} must be an object {{ \"root\": …, \"files\": […] }}.");
                    continue;
                }

                var root2 = Path(entry, "root", field + ".root", required: true);
                var files = Strings(entry, "files", field + ".files").Select(f => f.Replace('\\', '/')).ToArray();
                if (files.Length == 0)
                {
                    _errors.Add($"[config] {file}: {field}.files must list at least one proto relative to {field}.root.");
                }

                foreach (var bad in files.Where(f => f.StartsWith('/') || f.Split('/').Contains("..") || System.IO.Path.IsPathRooted(f) ||
                    !f.EndsWith(".proto", StringComparison.Ordinal)))
                {
                    _errors.Add($"[config] {file}: {field}.files '{bad}' must be a .proto path inside its root (relative, no '..').");
                }

                if (root2 is not null)
                {
                    sets.Add(new ProtoSet(root2, files));
                }
            }

            return [.. sets];
        }

        private string ClassName(JsonElement root, string name, string fallback)
        {
            var value = String(root, name, required: false) ?? fallback;
            if (!Names.IsIdentifier(value))
            {
                _errors.Add($"[config] {file}: {name} '{value}' is not a C# class name.");
            }

            return value;
        }

        private static string? Under(string outputRoot, string? relative)
        {
            if (relative is null)
            {
                return null;
            }

            return Full(System.IO.Path.Combine(outputRoot, Normalize(relative)));
        }

        private string? Path(JsonElement element, string name, string field, bool required) =>
            String(element, name, required, field) is { } value ? Resolve(value) : null;

        private string Resolve(string value) => Full(System.IO.Path.Combine(folder, Normalize(value)));

        private string? String(JsonElement element, string name, bool required, string? field = null)
        {
            field ??= name;
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (required)
                {
                    _errors.Add($"[config] {file}: {field} is required.");
                }

                return null;
            }

            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            {
                _errors.Add($"[config] {file}: {field} must be a non-empty string.");
                return null;
            }

            return text;
        }

        private string[] Strings(JsonElement element, string name, string? field = null)
        {
            field ??= name;
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 }))
            {
                _errors.Add($"[config] {file}: {field} must be an array of non-empty strings.");
                return [];
            }

            return value.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }
    }
}
