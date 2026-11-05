using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace GameServer.Platform.Web.OpenApi;

/// <summary>
/// Describes each GM operation for a generic console (slice 017, data-model §1.1): operation id = route name, area tag, every
/// template parameter, the <c>X-Request-Id</c> header and documented query parameters, and the <c>x-gm-kind</c>,
/// <c>x-gm-operation</c>, <c>x-gm-target</c>, <c>x-gm-confirm</c> and <c>x-gm-paged</c> keys. Reads endpoint metadata only.
/// </summary>
internal sealed partial class GmOperationTransformer : IOpenApiOperationTransformer
{
    public const string KindCommand = "command";
    public const string KindQuery = "query";
    public const string KindAction = "action";
    public const string TargetPlayer = "player";
    public const string TargetGlobal = "global";

    /// <summary>Same rule as <see cref="GmHttp.RequirePlayerId"/>.</summary>
    public const string PlayerIdPattern = "^[A-Za-z0-9_-]{1,64}$";

    private const string PlayerIdTemplate = "{" + GmCommandEndpointFactory.PlayerIdRouteValue + "}";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var description = context.Description;
        var metadata = description.ActionDescriptor.EndpointMetadata;
        var method = description.HttpMethod ?? HttpMethods.Get;
        var template = RouteDeclaration.CanonicalTemplate(description.ActionDescriptor.AttributeRouteInfo?.Template ?? description.RelativePath ?? string.Empty);

        var name = metadata.OfType<RouteDeclarationMetadata>().FirstOrDefault()?.Declaration.Name
            ?? context.ApplicationServices.GetRequiredService<WireCatalog>().FindRouteName(method, template);
        if (name is not null)
        {
            operation.OperationId = name;
            if (!metadata.OfType<ITagsMetadata>().Any())
            {
                operation.Tags = new HashSet<OpenApiTagReference> { new(AreaOf(name), context.Document) };
            }
        }

        operation.Parameters ??= [];
        AddPathParameters(operation.Parameters, template);
        if (metadata.OfType<GmRequestIdMetadata>().Any())
        {
            AddRequestIdHeader(operation.Parameters);
        }

        var paged = metadata.OfType<GmPagedMetadata>().Any();
        foreach (var query in metadata.OfType<GmQueryParameterMetadata>())
        {
            AddQueryParameter(operation.Parameters, query, paged);
        }

        if (operation.Parameters.Count == 0)
        {
            operation.Parameters = null;
        }

        var command = metadata.OfType<CommandOperationMetadata>().FirstOrDefault()?.Operation;
        var isGet = HttpMethods.IsGet(method);
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-gm-kind"] = Extension(command is not null ? KindCommand : isGet ? KindQuery : KindAction);
        if (command is not null)
        {
            operation.Extensions["x-gm-operation"] = Extension(command);
        }

        operation.Extensions["x-gm-target"] = Extension(template.Contains(PlayerIdTemplate, StringComparison.Ordinal) ? TargetPlayer : TargetGlobal);
        if (!isGet)
        {
            var typed = metadata.OfType<GmConfirmMetadata>().LastOrDefault()?.Confirm == GmConfirm.Typed;
            operation.Extensions["x-gm-confirm"] = Extension(typed ? "typed" : "simple");
        }

        if (paged)
        {
            operation.Extensions["x-gm-paged"] = new JsonNodeExtension(JsonValue.Create(true));
        }

        return Task.CompletedTask;
    }

    /// <summary>The route-name segment before the first dot; the whole name when it has none (D1).</summary>
    internal static string AreaOf(string routeName)
    {
        var dot = routeName.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? routeName : routeName[..dot];
    }

    private static JsonNodeExtension Extension(string value) => new(JsonValue.Create(value));

    /// <summary>Every <c>{name}</c> of the template (constraints, defaults and catch-all markers removed) as a required path parameter.</summary>
    private static void AddPathParameters(IList<IOpenApiParameter> parameters, string template)
    {
        foreach (Match match in TemplateParameter().Matches(template))
        {
            var name = match.Groups["name"].Value;
            var existing = parameters.FirstOrDefault(p => p.In == ParameterLocation.Path && string.Equals(p.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                parameters.Add(new OpenApiParameter
                {
                    Name = name,
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = PathSchema(name),
                });
            }
            else if (existing is OpenApiParameter parameter && name == GmCommandEndpointFactory.PlayerIdRouteValue)
            {
                parameter.Schema = PathSchema(name);
            }
        }
    }

    private static OpenApiSchema PathSchema(string name) => new()
    {
        Type = JsonSchemaType.String,
        Pattern = name == GmCommandEndpointFactory.PlayerIdRouteValue ? PlayerIdPattern : null,
    };

    private static void AddRequestIdHeader(IList<IOpenApiParameter> parameters)
    {
        if (parameters.Any(p => p.In == ParameterLocation.Header && string.Equals(p.Name, CorrelationMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        parameters.Add(new OpenApiParameter
        {
            Name = CorrelationMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        });
    }

    private static void AddQueryParameter(IList<IOpenApiParameter> parameters, GmQueryParameterMetadata query, bool paged)
    {
        if (parameters.Any(p => p.In == ParameterLocation.Query && string.Equals(p.Name, query.Name, StringComparison.Ordinal)))
        {
            return;
        }

        var pageSize = paged && query.Name == GmPresentationExtensions.PageSizeParameter;
        parameters.Add(new OpenApiParameter
        {
            Name = query.Name,
            In = ParameterLocation.Query,
            Required = query.Required,
            Description = query.Description,
            Schema = pageSize
                ? new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = PagedQuery.MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                : new OpenApiSchema { Type = JsonSchemaType.String, MinLength = query.MinLength, MaxLength = query.MaxLength },
        });
    }

    [GeneratedRegex(@"\{\**(?<name>[A-Za-z_][A-Za-z0-9_]*)[^}]*\}")]
    private static partial Regex TemplateParameter();
}
