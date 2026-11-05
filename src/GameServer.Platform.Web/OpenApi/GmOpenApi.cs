using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace GameServer.Platform.Web.OpenApi;

/// <summary>
/// OpenAPI document of the GM API (deliverable #8, FR-020 of slice 007; slice 017): every <c>/gm/**</c> endpoint, with the GM
/// permission it needs (<c>x-gm-permission</c>), whether a cookie caller must send <c>X-CSRF-Token</c> (<c>x-gm-csrf</c>) and
/// the <c>x-gm-*</c> keys of a generic console. Registered in every environment and served by the catalog route
/// (<see cref="GmCatalogDocument"/>); the route <see cref="Route"/> serves it in Development/Test only. The committed copy is
/// <c>docs/api/gm-openapi.json</c>.
/// </summary>
public static class GmOpenApi
{
    public const string DocumentName = "gm";
    public const string Route = "/gm/v1/openapi/{documentName}.json";
    public const string DocumentPath = "/gm/v1/openapi/gm.json";

    /// <summary>Registers the GM document with the GM transformers (every environment since slice 017).</summary>
    public static void Add(IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith("gm/", StringComparison.OrdinalIgnoreCase) == true;
            options.AddDocumentTransformer<GmDocumentTransformer>();
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var profile = metadata.OfType<SecurityProfileMetadata>().FirstOrDefault()?.Profile;
                var permission = metadata.OfType<RequiredPermissionMetadata>().FirstOrDefault()?.Permission;
                var method = context.Description.HttpMethod ?? "GET";
                var csrf = CsrfValidation.IsUnsafe(method)
                    && (profile == SecurityProfile.Gm || metadata.OfType<CsrfRequiredMetadata>().Any());
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions["x-gm-permission"] = new JsonNodeExtension(permission is null ? JsonValue.Create("public") : JsonValue.Create(permission));
                operation.Extensions["x-gm-csrf"] = new JsonNodeExtension(JsonValue.Create(csrf));
                operation.Responses ??= [];
                operation.Responses["4XX"] = new OpenApiResponse { Description = "Problem Details (application/problem+json) with `code` and `codeNumber` from the error-code catalog." };
                operation.Responses["5XX"] = new OpenApiResponse { Description = "Problem Details; `retryable` tells whether the same request may be retried." };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer<GmOperationTransformer>();
            options.AddSchemaTransformer<GmSchemaTransformer>();
        });
    }

    /// <summary>Maps the document (Development/Test only) as a Public GM endpoint.</summary>
    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapOpenApi(Route).AsPublicEndpoint(protobuf: false).WithDeclaration(PlatformRoutes.GmOpenApi);
}
