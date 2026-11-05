using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GameServer.Platform.Web.OpenApi;

/// <summary>
/// Document-level rules of the GM document (slice 017): the fixed <c>info</c> block, no <c>servers</c> entry (the bytes must not
/// depend on the environment or the request host, D4), and the document tag list rebuilt from the operations' area tags.
/// </summary>
internal sealed class GmDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string Title = "Game Server GM API";

    public const string Description =
        "JSON API for GM tools. Errors are Problem Details with a `code` (error-code catalog). " +
        "Cookie-authenticated mutations need the `X-CSRF-Token` header from GET /gm/v1/auth/csrf; " +
        "GM commands need an `X-Request-Id` UUID and a `reason`. " +
        "Operations carry `x-gm-*` keys for generic consoles: `x-gm-permission`, `x-gm-csrf`, `x-gm-kind`, `x-gm-operation`, " +
        "`x-gm-target`, `x-gm-confirm` and `x-gm-paged`; body properties may carry `x-gm-options` (an option route name).";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo { Title = Title, Version = "v1", Description = Description };
        document.Servers = null;

        // The generator lists the tags it gave operations before the operation transformer replaced them by area tags.
        var names = (document.Paths?.Values ?? Enumerable.Empty<IOpenApiPathItem>())
            .SelectMany(path => path.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            .SelectMany(operation => operation.Tags ?? new HashSet<OpenApiTagReference>())
            .Select(tag => tag.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        document.Tags = names.Length == 0 ? null : new HashSet<OpenApiTag>(names.Select(name => new OpenApiTag { Name = name }));
        DropNullFromRequiredObjects(document);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The generator wraps a nullable object property as <c>oneOf: [null, $ref]</c> once references exist (after the schema
    /// transformers). In a request body whose <c>required</c> lists it, the server refuses <c>null</c> (<c>[Required]</c>), so the
    /// use site keeps the reference only (review m5). Response schemas are left as generated.
    /// </summary>
    private static void DropNullFromRequiredObjects(OpenApiDocument document)
    {
        foreach (var body in (document.Components?.Schemas?.Values ?? Enumerable.Empty<IOpenApiSchema>()).OfType<OpenApiSchema>())
        {
            if (body.Metadata?.ContainsKey(GmSchemaTransformer.RequestBodyMarker) != true || body.Required is null || body.Properties is null)
            {
                continue;
            }

            foreach (var name in body.Required)
            {
                if (body.Properties.TryGetValue(name, out var use)
                    && use is OpenApiSchema { OneOf: { Count: 2 } branches }
                    && branches.FirstOrDefault(b => b is OpenApiSchema { Type: JsonSchemaType.Null }) is { } nullBranch)
                {
                    body.Properties[name] = branches.First(b => !ReferenceEquals(b, nullBranch));
                }
            }
        }
    }
}
