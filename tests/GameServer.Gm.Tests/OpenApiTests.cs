using System.Text.Json.Nodes;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// US6 / deliverable #8 and slice 017 (FR-053, FR-055): the GM API contract. The GM document exists in every environment and
/// the catalog route serves it; every GM endpoint is documented with its permission and CSRF requirement, and the committed copy
/// (docs/api/gm-openapi.json) equals the host's document through the test kit call a game uses (<c>GmOpenApiSnapshot</c>).
/// Set UPDATE_OPENAPI=1 to rewrite the committed copy after an intended change (review the diff before committing).
/// </summary>
public sealed class OpenApiTests
{
    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    [Fact]
    public async Task Every_gm_endpoint_is_documented_with_permission_and_csrf_and_matches_the_committed_copy()
    {
        // SSO configured so its endpoints are part of the contract too.
        await using var host = await GmDocumentShapeTests.StartAsync();
        var document = JsonNode.Parse(await GmOpenApiSnapshot.GetDocumentAsync(host.Services))!;
        var paths = document["paths"]!.AsObject();

        // Excluded from the description (gm.ui, the console catalog route) stay out of the document (FR-009).
        var gmEndpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => ("/" + e.RoutePattern.RawText!.TrimStart('/')).StartsWith("/gm/", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
            .ToArray();
        gmEndpoints.Length.ShouldBeGreaterThan(15);
        foreach (var endpoint in gmEndpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var operation = paths[route]?[method.ToLowerInvariant()];
                operation.ShouldNotBeNull($"{method} {route} is missing from the GM OpenAPI document");
                var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission ?? "public";
                operation["x-gm-permission"]!.GetValue<string>().ShouldBe(permission, $"{method} {route}");
                operation["x-gm-csrf"]!.GetValue<bool>().ShouldBe(method != "GET", $"{method} {route}");
            }
        }

        paths.ContainsKey(Modules.Gm.GmModule.ConsoleCatalogRoute).ShouldBeFalse();
        paths.ContainsKey(Modules.Gm.GmModule.UiRoute).ShouldBeFalse();

        var committed = RepositoryFile(Path.Combine("docs", "api", "gm-openapi.json"));
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await GmOpenApiSnapshot.WriteAsync(host, committed);
        }

        (await GmOpenApiSnapshot.DiffAsync(host, committed)).ShouldBeEmpty();
    }
}
