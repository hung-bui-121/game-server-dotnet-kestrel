using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Platform;
using GameServer.Platform.Web.OpenApi;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 T030 (FR-030..FR-033, FR-008, US3-1..US3-5): <c>GET /gm/v1/console/openapi.json</c> serves the GM document to every
/// staff with <c>gm.access</c>, unfiltered, with a strong entity tag, <c>Cache-Control: private, no-cache</c> and 304 on a match.
/// </summary>
public sealed class GmCatalogRouteTests : IAsyncLifetime
{
    private const string AccessOnly = "access-only";

    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await GmTestHost.StartAsync(s =>
        {
            const string prefix = "Gm:Bootstrap:Staff:8:";
            s[prefix + "StaffId"] = AccessOnly;
            s[prefix + "DisplayName"] = "Staff access only";
            s[prefix + "Password"] = GmTestHost.Password;
            s[prefix + "Disabled"] = "false";
            s[prefix + "Permissions:0"] = GmPermissions.Access;
        });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Anonymous_is_401_and_staff_of_another_environment_403()
    {
        (await _host.Browser().GetAsync(GmModule.ConsoleCatalogRoute).ShouldBeAsync(401))["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");

        var other = await _host.GmClientAsync("other-env");
        (await other.GetAsync(GmModule.ConsoleCatalogRoute).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task Gm_access_alone_gets_the_canonical_document_with_its_cache_headers()
    {
        var staff = await _host.GmClientAsync(AccessOnly);

        var response = await staff.GetAsync(GmModule.ConsoleCatalogRoute);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/json");
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        etag.Tag.Length.ShouldBe(34);
        response.Headers.CacheControl!.ToString().ShouldBe("no-cache, private");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var catalog = await _host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None);
        bytes.ShouldBe(catalog.Bytes.ToArray());
        etag.Tag.ShouldBe(catalog.ETag);

        var route = JsonNode.Parse(await _host.Client().GetStringAsync(Platform.Web.OpenApi.GmOpenApi.DocumentPath));
        JsonNode.DeepEquals(JsonNode.Parse(bytes), route).ShouldBeTrue();
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("W/{0}")]
    [InlineData("\"other\", {0}")]
    [InlineData("*")]
    public async Task A_matching_if_none_match_is_304_without_a_body(string format)
    {
        var staff = await _host.GmClientAsync(AccessOnly);
        var etag = (await _host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None)).ETag;
        var request = new HttpRequestMessage(HttpMethod.Get, GmModule.ConsoleCatalogRoute);
        request.Headers.TryAddWithoutValidation("If-None-Match", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, etag));

        var response = await staff.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        response.Headers.ETag!.Tag.ShouldBe(etag);
        response.Headers.CacheControl!.ToString().ShouldBe("no-cache, private");
    }

    [Fact]
    public async Task An_unknown_tag_gets_the_document()
    {
        var staff = await _host.GmClientAsync(AccessOnly);
        var request = new HttpRequestMessage(HttpMethod.Get, GmModule.ConsoleCatalogRoute);
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"0123456789abcdef0123456789abcdef\"");

        (await staff.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_document_has_no_version_no_servers_and_is_the_same_for_every_staff()
    {
        var runtime = _host.Services.GetRequiredService<GameServerRuntimeInfo>();
        var admin = await (await _host.GmClientAsync("admin")).GetByteArrayAsync(GmModule.ConsoleCatalogRoute);
        var accessOnly = await (await _host.GmClientAsync(AccessOnly)).GetByteArrayAsync(GmModule.ConsoleCatalogRoute);

        accessOnly.ShouldBe(admin);
        var text = Encoding.UTF8.GetString(admin);
        text.ShouldNotContain(GameServerVersion.FrameworkInformational);
        text.ShouldNotContain("\"" + runtime.GameVersion + "\"");
        text.ShouldNotContain("\"servers\"");
        text.ShouldNotContain(GmModule.ConsoleCatalogRoute);
    }
}
