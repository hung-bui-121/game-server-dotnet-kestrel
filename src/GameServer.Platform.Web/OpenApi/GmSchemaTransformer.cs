using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace GameServer.Platform.Web.OpenApi;

/// <summary>
/// Writes the rules the server enforces into the schemas of GM request bodies (slice 017, research R6, data-model §1.2), from the
/// same <see cref="GmBodyContract"/> walk as <see cref="GmBodyValidator"/>: DataAnnotations keywords on each property schema
/// (overwriting the generator, which reads property-target attributes only, V1), <c>x-gm-options</c>, the real <c>required</c>
/// list of request bodies (without <c>null</c> in the type of a <c>[Required]</c> property), and the <c>reason</c> rule of
/// command bodies. Response schemas keep the generator's <c>required</c>. Enums are described as the GM wire writes them
/// (<c>GameServerJson.Options</c>: camelCase strings), whatever the host's HTTP JSON options say.
/// </summary>
internal sealed class GmSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>At least one character that is not white space (the reason rule and <c>[Required]</c> on strings).</summary>
    private const string NotBlankPattern = "\\S";

    /// <summary>Schema metadata (not serialized) marking a GM request body for the document transformer.</summary>
    public const string RequestBodyMarker = "gm.request-body";

    private HashSet<Type>? _requestBodies;

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        // A custom converter writes its own representation: leave the generator's schema (known limit, PCR gm).
        if (context.JsonPropertyInfo?.CustomConverter is null)
        {
            DescribeEnum(schema, context.JsonTypeInfo.Type);
        }

        if (context.JsonPropertyInfo is { } property)
        {
            TransformProperty(schema, property, context.JsonTypeInfo);
        }

        // A type schema, or the schema of an object-typed property, which is the nested type's own schema (no separate call).
        if ((context.JsonPropertyInfo is null || context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object)
            && RequestBodies(context.ApplicationServices).Contains(context.JsonTypeInfo.Type))
        {
            schema.Required = RequiredOf(GmBodyContract.For(context.JsonTypeInfo.Type));
            schema.Metadata ??= new Dictionary<string, object>();
            schema.Metadata[RequestBodyMarker] = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The generator reads enums with the host's HTTP JSON options (integers by default); every GM route writes and reads them with
    /// <see cref="GameServerJson.Options"/>: the schema lists the exact strings that options produce (review r1 m1).
    /// </summary>
    private static void DescribeEnum(OpenApiSchema schema, Type type)
    {
        var enumType = Nullable.GetUnderlyingType(type) ?? type;

        // [Flags] values serialize as comma lists of names ("read, write"), which no closed list describes (known limit, PCR gm).
        if (!enumType.IsEnum || enumType.IsDefined(typeof(FlagsAttribute), inherit: false)
            || schema.Type is not { } current || !current.HasFlag(JsonSchemaType.Integer))
        {
            return;
        }

        schema.Type = current.HasFlag(JsonSchemaType.Null) ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.Format = null;
        schema.Pattern = null;
        schema.Enum = Enum.GetValues(enumType).Cast<object>()
            .Select(value => JsonSerializer.SerializeToNode(value, enumType, GameServerJson.Options)!)
            .DistinctBy(node => node.ToJsonString())
            .ToList();
    }

    /// <summary>The properties the server requires: <c>[Required]</c> ones and the reason of a command body, in declaration order.</summary>
    private static HashSet<string>? RequiredOf(GmBodyContract contract)
    {
        var command = typeof(IGmCommandBody).IsAssignableFrom(contract.Type);
        var required = contract.Properties
            .Where(p => p.IsRequired || (command && IsReason(p)))
            .Select(p => p.JsonName)
            .ToArray();
        return required.Length == 0 ? null : new HashSet<string>(required, StringComparer.Ordinal);
    }

    private static bool IsReason(GmBodyProperty property) => property.ClrName == nameof(IGmCommandBody.Reason);


    private static void TransformProperty(OpenApiSchema schema, JsonPropertyInfo info, JsonTypeInfo propertyType)
    {
        var declaring = GmBodyContract.For(info.DeclaringType);
        var property = declaring.Properties.FirstOrDefault(p => p.JsonName == info.Name);
        if (property is null)
        {
            return;
        }

        if (propertyType.Kind == JsonTypeInfoKind.Object)
        {
            // The schema of an object-typed property is the nested type's own schema (one shared component): use-site rules
            // never go there (research R6 trap); the generator's are removed.
            if (property.Validations.Count > 0 || property.Description is not null)
            {
                ClearKeywords(schema);
            }

            return;
        }

        if (typeof(IGmCommandBody).IsAssignableFrom(info.DeclaringType) && IsReason(property))
        {
            schema.Type = JsonSchemaType.String;
            schema.MinLength = 1;
            schema.MaxLength = GmCommandEndpointFactory.MaxReasonLength;
            schema.Pattern = NotBlankPattern;
        }

        var collection = propertyType.Kind == JsonTypeInfoKind.Enumerable;
        foreach (var attribute in property.Validations)
        {
            Apply(schema, attribute, collection, info.PropertyType);
        }

        // [Required] refuses "" and white space on a string by default: the schema says so (review M3).
        if (property.Validations.OfType<RequiredAttribute>().Any(r => !r.AllowEmptyStrings)
            && (Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType) == typeof(string))
        {
            schema.MinLength ??= 1;
            schema.Pattern ??= NotBlankPattern;
        }

        if (property.Description is not null)
        {
            schema.Description = property.Description;
        }

        if (property.IsRequired && schema.Type is { } type && type.HasFlag(JsonSchemaType.Null))
        {
            schema.Type = type & ~JsonSchemaType.Null;
        }

        if (property.OptionsRoute is not null)
        {
            SetOptions(collection && schema.Items is { } items ? items : schema, property.OptionsRoute);
        }
    }

    /// <summary><c>x-gm-options</c> on the property schema, or on its items (an inline schema or a reference) for a collection.</summary>
    private static void SetOptions(IOpenApiSchema target, string route)
    {
        var extension = new JsonNodeExtension(JsonValue.Create(route));
        switch (target)
        {
            case OpenApiSchema inline:
                inline.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                inline.Extensions["x-gm-options"] = extension;
                break;
            case OpenApiSchemaReference reference:
                reference.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                reference.Extensions["x-gm-options"] = extension;
                break;
        }
    }

    private static void Apply(OpenApiSchema schema, ValidationAttribute attribute, bool collection, Type propertyType)
    {
        switch (attribute)
        {
            case RangeAttribute range:
                ApplyRange(schema, range, propertyType);
                break;
            case StringLengthAttribute length:
                schema.MaxLength = length.MaximumLength;
                schema.MinLength = length.MinimumLength > 0 ? length.MinimumLength : null;
                break;
            case LengthAttribute length:
                SetLength(schema, collection, length.MinimumLength, length.MaximumLength);
                break;
            case MinLengthAttribute min:
                SetLength(schema, collection, min.Length, max: null);
                break;
            case MaxLengthAttribute max when max.Length > 0:
                SetLength(schema, collection, min: null, max.Length);
                break;
            case RegularExpressionAttribute regex:
                // .NET matches the whole value and lets "" through; JSON Schema `pattern` is a search (review r1 M1).
                schema.Pattern = "^(?:" + regex.Pattern + ")?$";
                break;
        }
    }

    private static void SetLength(OpenApiSchema schema, bool collection, int? min, int? max)
    {
        if (collection)
        {
            schema.MinItems = min ?? schema.MinItems;
            schema.MaxItems = max ?? schema.MaxItems;
        }
        else
        {
            schema.MinLength = min ?? schema.MinLength;
            schema.MaxLength = max ?? schema.MaxLength;
        }
    }

    private static void ApplyRange(OpenApiSchema schema, RangeAttribute range, Type propertyType)
    {
        var integer = IsInteger(Nullable.GetUnderlyingType(propertyType) ?? propertyType);
        var min = Bound(range.Minimum, integer);
        var max = Bound(range.Maximum, integer);
        if (min is null || max is null)
        {
            return;
        }

        schema.Minimum = range.MinimumIsExclusive ? null : min;
        schema.ExclusiveMinimum = range.MinimumIsExclusive ? min : null;
        schema.Maximum = range.MaximumIsExclusive ? null : max;
        schema.ExclusiveMaximum = range.MaximumIsExclusive ? max : null;
    }

    /// <summary>
    /// A numeric bound as JSON number text; null for a non-numeric operand (dates, custom types: not shown). A double bound of an
    /// integer property is written as the integer it means: <c>[Range(0, long.MaxValue)]</c> binds the double overload, whose
    /// maximum reads <c>9.223372036854776E+18</c>, and every <c>long</c> passes it.
    /// </summary>
    private static string? Bound(object bound, bool integer) => bound switch
    {
        int value => value.ToString(CultureInfo.InvariantCulture),
        long value => value.ToString(CultureInfo.InvariantCulture),
        double value when integer && value >= long.MaxValue => long.MaxValue.ToString(CultureInfo.InvariantCulture),
        double value when integer && value <= long.MinValue => long.MinValue.ToString(CultureInfo.InvariantCulture),
        double value when integer && Math.Floor(value) == value => ((long)value).ToString(CultureInfo.InvariantCulture),
        double value => value.ToString("R", CultureInfo.InvariantCulture),
        decimal value => value.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static void ClearKeywords(OpenApiSchema schema)
    {
        schema.Minimum = null;
        schema.Maximum = null;
        schema.ExclusiveMinimum = null;
        schema.ExclusiveMaximum = null;
        schema.MinLength = null;
        schema.MaxLength = null;
        schema.MinItems = null;
        schema.MaxItems = null;
        schema.Pattern = null;
        schema.Description = null;
    }

    /// <summary>
    /// The types read as GM request bodies (<c>Accepts</c> of <c>/gm/**</c> endpoints) and the object types nested in them to the
    /// validated depth: computed once per document.
    /// </summary>
    private HashSet<Type> RequestBodies(IServiceProvider services)
    {
        if (_requestBodies is not null)
        {
            return _requestBodies;
        }

        var types = new HashSet<Type>();
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("gm/", StringComparison.OrdinalIgnoreCase) == true);
        foreach (var accepts in endpoints.SelectMany(e => e.Metadata.GetOrderedMetadata<IAcceptsMetadata>()))
        {
            if (accepts.RequestType is { } type)
            {
                AddWithNested(types, GmBodyContract.For(type));
            }
        }

        _requestBodies = types;
        return types;
    }

    private static void AddWithNested(HashSet<Type> types, GmBodyContract contract)
    {
        if (!types.Add(contract.Type))
        {
            return;
        }

        foreach (var nested in contract.Properties.Select(p => p.Nested).OfType<GmBodyContract>())
        {
            AddWithNested(types, nested);
        }
    }
}
