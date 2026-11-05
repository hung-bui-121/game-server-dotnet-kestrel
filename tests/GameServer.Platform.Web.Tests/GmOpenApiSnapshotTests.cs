using System.Text;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.OpenApi;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T045 (SC-005, FR-050..FR-052, US5-1..US5-4): the kit writes the catalog's canonical bytes and reports the first
/// differing line of a committed copy, ignoring line ends; a missing file is a message.
/// </summary>
public sealed class GmOpenApiSnapshotTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-gm-snapshot-" + Guid.NewGuid().ToString("N"));

    private TestHost _host = null!;

    private string Committed => Path.Combine(_folder, "docs", "api", "gm-openapi.json");

    public async ValueTask InitializeAsync() =>
        _host = await SampleTestHost.StartAsync(builder =>
        {
            SampleTestHost.DefaultModules(builder);
            builder.AddModule<GmModule>();
        });

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Write_then_diff_is_empty_and_the_file_holds_the_canonical_bytes()
    {
        await GmOpenApiSnapshot.WriteAsync(_host, Committed);

        (await GmOpenApiSnapshot.DiffAsync(_host, Committed)).ShouldBeEmpty();
        var bytes = await File.ReadAllBytesAsync(Committed);
        bytes.ShouldBe((await _host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None)).Bytes.ToArray());
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        bytes.ShouldNotContain((byte)'\r');
        bytes[^1].ShouldBe((byte)'\n');
        bytes[^2].ShouldNotBe((byte)'\n');
        File.Exists(Committed + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task A_one_character_change_is_reported_at_its_line_with_both_texts()
    {
        await GmOpenApiSnapshot.WriteAsync(_host, Committed);
        var lines = (await File.ReadAllTextAsync(Committed)).Split('\n');
        var middle = lines.Length / 2;
        var last = lines.Length - 2;

        foreach (var index in new[] { 0, middle, last })
        {
            var changed = (string[])lines.Clone();
            changed[index] = changed[index].Length == 0 ? "x" : changed[index][..^1] + (changed[index][^1] == 'x' ? 'y' : 'x');
            await File.WriteAllTextAsync(Committed, string.Join('\n', changed));

            var message = await GmOpenApiSnapshot.DiffAsync(_host, Committed);

            message.ShouldBe(
                $"GM OpenAPI document differs from {Committed} at line {index + 1}:\n" +
                $"  committed: {Cut(changed[index])}\n" +
                $"  host:      {Cut(lines[index])}\n" +
                "Review the change, then rewrite the file with GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1).");
        }
    }

    [Fact]
    public async Task A_crlf_copy_compares_equal()
    {
        await GmOpenApiSnapshot.WriteAsync(_host, Committed);
        var text = await File.ReadAllTextAsync(Committed);
        await File.WriteAllTextAsync(Committed, text.Replace("\n", "\r\n", StringComparison.Ordinal));

        (await GmOpenApiSnapshot.DiffAsync(_host, Committed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_truncated_copy_reports_the_end_of_the_file()
    {
        await GmOpenApiSnapshot.WriteAsync(_host, Committed);
        var lines = (await File.ReadAllTextAsync(Committed)).Split('\n');
        await File.WriteAllTextAsync(Committed, string.Join('\n', lines.Take(10)));

        var message = await GmOpenApiSnapshot.DiffAsync(_host, Committed);

        message.ShouldBe(
            $"GM OpenAPI document differs from {Committed} at line 11:\n" +
            "  committed: (end of file)\n" +
            $"  host:      {Cut(lines[10])}\n" +
            "Review the change, then rewrite the file with GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1).");
    }

    [Fact]
    public async Task A_missing_file_is_a_message()
    {
        var missing = Path.Combine(_folder, "nowhere", "gm-openapi.json");

        (await GmOpenApiSnapshot.DiffAsync(_host, missing)).ShouldBe(
            $"GM OpenAPI document {missing} does not exist. Create it with GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1).");
    }

    [Fact]
    public async Task The_document_text_is_what_the_catalog_route_serves()
    {
        var served = await _host.GmClient("viewer", GmPermissions.Access).GetByteArrayAsync(GmModule.ConsoleCatalogRoute);

        (await GmOpenApiSnapshot.GetDocumentAsync(_host.Services)).ShouldBe(Encoding.UTF8.GetString(served));
    }

    private static string Cut(string line) => line.Length <= 200 ? line : line[..200];
}
