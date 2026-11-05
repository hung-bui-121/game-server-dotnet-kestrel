using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.OpenApi;
using GameServer.Platform.Web.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T010 (FR-030..FR-033, A1, A2, D12): the GM document is registered in every environment and its canonical bytes are
/// generated once per process, on first use, equal to the Development route's document; a failed generation is not kept.
/// </summary>
public sealed class GmCatalogDocumentTests
{
    [Fact]
    public async Task A_host_without_development_tools_produces_the_document()
    {
        // Built, not started: a Staging host cannot start on the in-memory store, and the registration is what is checked
        // (before slice 017 the GM document existed only with development tools). Endpoints join the generator at start.
        await using var app = SampleTestHost.BuildApp(settings: s => s["GameServer:DeploymentEnvironment"] = "Staging");
        app.Services.GetRequiredService<GameServerOptions>().AllowsDevelopmentTools.ShouldBeFalse();

        var catalog = await app.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None);

        var document = JsonNode.Parse(catalog.Bytes.Span)!;
        document["openapi"]!.GetValue<string>().ShouldStartWith("3.1");
        document["info"]!["title"]!.GetValue<string>().ShouldBe("Game Server GM API");
        document["paths"].ShouldBeOfType<JsonObject>();
    }

    [Fact]
    public async Task The_bytes_equal_the_development_route_document()
    {
        await using var host = await SampleTestHost.StartAsync();

        var route = JsonNode.Parse(await host.Client().GetStringAsync(GmOpenApi.DocumentPath))!;
        route.AsObject().ContainsKey("servers").ShouldBeFalse();
        var catalog = await host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None);

        var bytes = catalog.Bytes.ToArray();
        Encoding.UTF8.GetString(bytes).ShouldBe(GmCatalogDocument.Canonicalize(route.ToJsonString()));
        JsonNode.Parse(bytes)!["paths"]!.AsObject().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_bytes_are_utf8_without_bom_with_lf_and_one_final_newline()
    {
        await using var host = await SampleTestHost.StartAsync();

        var bytes = (await host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None)).Bytes.ToArray();

        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        bytes.ShouldNotContain((byte)'\r');
        bytes[^1].ShouldBe((byte)'\n');
        bytes[^2].ShouldNotBe((byte)'\n');
    }

    [Fact]
    public async Task Concurrent_first_calls_share_one_generation()
    {
        await using var host = await SampleTestHost.StartAsync();
        var catalog = host.Services.GetRequiredService<GmCatalogDocument>();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => catalog.GetAsync(CancellationToken.None))));

        results.ShouldAllBe(r => ReferenceEquals(r, results[0]));
        results[0].ETag.Length.ShouldBe(34);
        results[0].ETag.ShouldMatch("^\"[0-9a-f]{32}\"$");
        var line = host.LogEntries.Where(e => e.Category == GmCatalogDocument.LogCategory).ShouldHaveSingleItem();
        line.Message.ShouldStartWith("GM catalog generated: ");
        line.Message.ShouldContain($" operations, {results[0].Bytes.Length} bytes, ");
    }

    [Fact]
    public async Task A_failed_generation_is_kept_for_the_hold_then_retried()
    {
        // Review r1 m5: a deterministic failure does not cost a generation per request.
        var clock = new FakeClock();
        var calls = 0;
        var catalog = new GmCatalogDocument(
            _ => ++calls == 1
                ? throw new InvalidOperationException("transformer bug")
                : Task.FromResult(("""{"openapi":"3.1.1","paths":{}}""", 0)),
            NullLoggerFactory.Instance,
            clock);

        await Should.ThrowAsync<InvalidOperationException>(() => catalog.GetAsync(CancellationToken.None));
        clock.Advance(GmCatalogDocument.FailureHold - TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<InvalidOperationException>(() => catalog.GetAsync(CancellationToken.None));
        calls.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await catalog.GetAsync(CancellationToken.None);
        var third = await catalog.GetAsync(CancellationToken.None);

        calls.ShouldBe(2);
        third.ShouldBeSameAs(second);
        Encoding.UTF8.GetString(second.Bytes.Span).ShouldBe("{\n  \"openapi\": \"3.1.1\",\n  \"paths\": {}\n}\n");
    }
}
