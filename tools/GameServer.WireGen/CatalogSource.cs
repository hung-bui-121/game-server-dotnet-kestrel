using System.Text.Json;

namespace GameServer.WireGen;

/// <summary>A route of the catalog export (data-model §3); <see cref="Methods"/> empty = any method.</summary>
internal sealed record ContractRoute(
    string Name,
    IReadOnlyList<string> Methods,
    string Template,
    string Profile,
    string Surface,
    string Owner,
    string Origin,
    string? Operation,
    bool ServiceOnly);

/// <summary>A section of the catalog export; <see cref="Message"/> null = mapper registered without its message type.</summary>
internal sealed record ContractSection(string Id, string Owner, string Origin, string? Message);

/// <summary>The parts of a <c>gameserver.wire-contract/1</c> export the generator uses; unknown properties are ignored.</summary>
internal sealed record WireContract(string FrameworkVersion, string ProtobufVersion, IReadOnlyList<ContractRoute> Routes, IReadOnlyList<ContractSection> Sections)
{
    public const string Format = "gameserver.wire-contract/1";
}

/// <summary>
/// Step 4 of contracts/wiregen-cli.md §4 (FR-030, research R9): the catalog from the host's own
/// <c>--export-wire-contract</c> mode (<c>dotnet run --project &lt;host&gt; --no-launch-profile [--no-build] -c &lt;cfg&gt; --
/// --environment &lt;env&gt; --export-wire-contract &lt;temp file&gt;</c>), or from a committed export file.
/// </summary>
internal static class CatalogSource
{
    /// <summary>A first build of a game host may take minutes; a hung host must not block CI forever.</summary>
    private static readonly TimeSpan HostTimeout = TimeSpan.FromMinutes(10);

    private const string FormatPrefix = "gameserver.wire-contract/";

    public static async Task<WireContract> LoadAsync(WireGenConfig config, string dotnet, string configuration, bool noBuild, string tempFolder)
    {
        if (config.CatalogFile is { } file)
        {
            return Read(file);
        }

        var export = Path.Combine(tempFolder, "wire-contract.json");
        var host = config.HostProject!;
        var arguments = new List<string> { "run", "--project", host, "--no-launch-profile" };
        if (noBuild)
        {
            arguments.Add("--no-build");
        }

        arguments.AddRange(["-c", configuration, "--", "--environment", config.Environment, "--export-wire-contract", export]);

        // The host's own folder: its content root and global.json, whatever the caller's current directory.
        var workingDirectory = Directory.Exists(host) ? host : Path.GetDirectoryName(host)!;
        var result = await ChildProcess.RunAsync(dotnet, arguments, workingDirectory, HostTimeout);
        if (result.ExitCode != 0 || !File.Exists(export))
        {
            throw new WireGenException(
                new[] { $"[catalog] the export of {host} (--environment {config.Environment}) exited with code {result.ExitCode}:" }
                    .Concat(result.FailureLines().Select(line => "  " + line))
                    .Append("Fix: correct the host as its lines say; the host Program must use GameServerCommandLine.Parse + RunGameServerAsync (contracts/wiregen-cli.md §1).")
                    .ToArray());
        }

        try
        {
            return Read(export);
        }
        finally
        {
            File.Delete(export);
        }
    }

    public static WireContract Read(string file)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new WireGenException($"[catalog] {file} cannot be read as a catalog export: {ex.Message} Fix: export it again with --export-wire-contract.");
        }

        var format = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        if (format is null || !format.StartsWith(FormatPrefix, StringComparison.Ordinal) || Major(format[FormatPrefix.Length..]) != "1")
        {
            throw new WireGenException(
                $"[catalog] {file} has format '{format}', this generator reads {WireContract.Format} (major 1). Fix: export the catalog with a host " +
                "built against the generator's framework checkout.");
        }

        try
        {
            return new WireContract(
                Text(root, "frameworkVersion"),
                Text(root, "protobufVersion"),
                Array(root, "routes").Select(Route).ToArray(),
                Array(root, "sections").Select(Section).ToArray());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new WireGenException($"[catalog] {file} is not a valid {WireContract.Format} export: {ex.Message} Fix: export it again with --export-wire-contract.");
        }
    }

    private static string Major(string version)
    {
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? version : version[..dot];
    }

    private static ContractRoute Route(JsonElement route) => new(
        Text(route, "name"),
        Array(route, "methods").Select(m => m.GetString() ?? throw new InvalidOperationException("a method is not a string")).ToArray(),
        Text(route, "template"),
        Text(route, "profile"),
        Text(route, "surface"),
        Text(route, "owner"),
        Text(route, "origin"),
        Optional(route, "operation"),
        route.TryGetProperty("serviceOnly", out var serviceOnly) && serviceOnly.ValueKind == JsonValueKind.True);

    private static ContractSection Section(JsonElement section) => new(
        Text(section, "id"),
        Text(section, "owner"),
        Text(section, "origin"),
        Optional(section, "message"));

    private static string Text(JsonElement element, string name) =>
        Optional(element, name) ?? throw new KeyNotFoundException($"property '{name}' is missing");

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];
}
