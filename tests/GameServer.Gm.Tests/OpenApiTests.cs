using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform.Web.OpenApi;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// US6 / deliverable #8: the GM API contract for frontend developers. Every GM endpoint is documented with its
/// permission and CSRF requirement, and the committed copy (docs/api/gm-openapi.json) matches the running host.
/// Set UPDATE_OPENAPI=1 to rewrite the committed copy after an intended change.
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
        await using var host = await GmTestHost.StartAsync(s =>
        {
            s["Gm:Oidc:Authority"] = "https://idp.example";
            s["Gm:Oidc:ClientId"] = "gm-console";
            s["Gm:Oidc:ClientSecret"] = "not-used";
        });
        var document = await host.Client().GetAsync(GmOpenApi.DocumentPath).ShouldBeAsync(200);
        var paths = document["paths"]!.AsObject();

        var gmEndpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => ("/" + e.RoutePattern.RawText!.TrimStart('/')).StartsWith("/gm/", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
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

        var committed = RepositoryFile(Path.Combine("docs", "api", "gm-openapi.json"));
        var current = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committed)!);
            await File.WriteAllTextAsync(committed, current);
        }

        File.Exists(committed).ShouldBeTrue("docs/api/gm-openapi.json is missing: run the tests once with UPDATE_OPENAPI=1.");
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(committed)), document)
            .ShouldBeTrue("The GM API changed: review it, then run the tests with UPDATE_OPENAPI=1 and commit docs/api/gm-openapi.json.");
    }
}
