using System.Text;
using GameServer.Platform.Web.OpenApi;

namespace GameServer.TestKit;

/// <summary>
/// CI-01 OpenAPI diff for a game (slice 017): writes or compares the committed GM document of a started host, so a change of the
/// GM contract (a new command, an annotation, a framework bump) shows up as a reviewed diff in the game's repository. Use the
/// game's real host: <c>await using var factory = new LocalFlavorFactory&lt;Program&gt;();</c> then
/// <c>(await GmOpenApiSnapshot.DiffAsync(factory.Services, committedPath)).ShouldBeEmpty();</c> where <c>committedPath</c> is the
/// absolute path of <c>docs/api/gm-openapi.json</c> in the game repository (a relative path resolves against the test output
/// folder), with a write mode
/// (an environment variable) that calls <see cref="WriteAsync(IServiceProvider, string, CancellationToken)"/> after an intended
/// change. The text is the canonical bytes the catalog route serves (<see cref="GmCatalogDocument"/>). No test framework is used.
/// </summary>
public static class GmOpenApiSnapshot
{
    private const int MaxLineText = 200;
    private const string EndOfFile = "(end of file)";
    private const string WriteHint = "GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1)";

    /// <summary>The canonical bytes the catalog route serves, as UTF-8 text.</summary>
    public static async Task<string> GetDocumentAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var bytes = await Catalog(services).GetAsync(cancellationToken);
        return Encoding.UTF8.GetString(bytes.Bytes.Span);
    }

    /// <summary>
    /// Writes the document to <paramref name="committedPath"/> (folder created; UTF-8 without BOM, LF, one final newline), through a
    /// temporary file replaced at the end, so a failed write never leaves half a document.
    /// </summary>
    public static async Task WriteAsync(IServiceProvider services, string committedPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(committedPath);
        var bytes = (await Catalog(services).GetAsync(cancellationToken)).Bytes;
        var target = Path.GetFullPath(committedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken);
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>
    /// Empty when the committed file equals the host's document (line ends ignored: a CRLF checkout compares equal); otherwise the
    /// first differing line with both texts and how to rewrite the file. A missing file is a message, not an exception.
    /// </summary>
    public static async Task<string> DiffAsync(IServiceProvider services, string committedPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(committedPath);
        var host = await GetDocumentAsync(services, cancellationToken);
        if (!File.Exists(committedPath))
        {
            return $"GM OpenAPI document {committedPath} does not exist. Create it with {WriteHint}.";
        }

        var committed = await File.ReadAllTextAsync(committedPath, cancellationToken);
        return Compare(committed, host, committedPath);
    }

    /// <inheritdoc cref="WriteAsync(IServiceProvider, string, CancellationToken)"/>
    public static Task WriteAsync(TestHost host, string committedPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return WriteAsync(host.Services, committedPath, cancellationToken);
    }

    /// <inheritdoc cref="DiffAsync(IServiceProvider, string, CancellationToken)"/>
    public static Task<string> DiffAsync(TestHost host, string committedPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return DiffAsync(host.Services, committedPath, cancellationToken);
    }

    private static GmCatalogDocument Catalog(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(GmCatalogDocument)) as GmCatalogDocument
            ?? throw new InvalidOperationException(
                "The host has no GmCatalogDocument: pass the services of a started game server host (AddGameServer).");
    }

    private static string Compare(string committed, string host, string committedPath)
    {
        var committedLines = committed.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var hostLines = host.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < Math.Max(committedLines.Length, hostLines.Length); i++)
        {
            var left = i < committedLines.Length ? committedLines[i] : null;
            var right = i < hostLines.Length ? hostLines[i] : null;
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return $"GM OpenAPI document differs from {committedPath} at line {i + 1}:\n" +
                    $"  committed: {Cut(left)}\n" +
                    $"  host:      {Cut(right)}\n" +
                    $"Review the change, then rewrite the file with {WriteHint}.";
            }
        }

        return string.Empty;
    }

    private static string Cut(string? line) =>
        line is null ? EndOfFile : line.Length <= MaxLineText ? line : line[..MaxLineText];
}
