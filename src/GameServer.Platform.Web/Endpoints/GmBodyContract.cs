using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>
/// The rules of a GM JSON body, read once per type from its System.Text.Json contract (slice 017, research R2): per JSON
/// property its DataAnnotations and <see cref="GmOptionsAttribute"/>, written on the property or on the matching positional
/// record parameter, and the contracts of nested objects and of object-list items down to <see cref="MaxDepth"/>. The same walk
/// feeds the GM document (schema transformer) and the server (<see cref="GmBodyValidator"/>), so both apply the same rules.
/// </summary>
internal sealed class GmBodyContract
{
    /// <summary>Levels walked: the body, its nested objects and theirs (the depth a GM console renders, design §9.2).</summary>
    public const int MaxDepth = 3;

    private static readonly ConcurrentDictionary<Type, GmBodyContract> Cache = new();

    private GmBodyContract(Type type, IReadOnlyList<GmBodyProperty> properties)
    {
        Type = type;
        Properties = properties;
        NeedsPresence = properties.Any(p => p.RequiresPresence || p.Nested?.NeedsPresence == true);
        HasRules = properties.Any(p => p.Validations.Count > 0 || p.RequiresPresence || p.Nested?.HasRules == true);
    }

    public Type Type { get; }

    public IReadOnlyList<GmBodyProperty> Properties { get; }

    /// <summary>
    /// True when this contract or a nested one has a non-nullable value type, whose absence the deserialized value cannot show
    /// (it reads as the default): the validator then checks the JSON for presence (research R3, review r1 m3).
    /// </summary>
    public bool NeedsPresence { get; }

    /// <summary>True when this contract or a nested one has a rule (attribute or presence): otherwise the validator skips the walk.</summary>
    public bool HasRules { get; }

    /// <summary>The contract of <paramref name="type"/> (cached per type).</summary>
    public static GmBodyContract For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, static t => Build(t, depth: 1));
    }

    private static GmBodyContract Build(Type type, int depth)
    {
        var typeInfo = GameServerJson.Options.GetTypeInfo(type);
        var properties = new List<GmBodyProperty>();
        if (typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in typeInfo.Properties)
            {
                if (property.Get is { } getter)
                {
                    properties.Add(BuildProperty(property, getter, depth));
                }
            }
        }

        return new GmBodyContract(type, properties);
    }

    private static GmBodyProperty BuildProperty(JsonPropertyInfo property, Func<object, object?> getter, int depth)
    {
        var sources = new[] { property.AttributeProvider, property.AssociatedParameter?.AttributeProvider };
        var validations = sources
            .SelectMany(s => s?.GetCustomAttributes(typeof(ValidationAttribute), inherit: true) ?? [])
            .Cast<ValidationAttribute>()
            .Distinct()
            .ToArray();
        var options = Single<GmOptionsAttribute>(sources);
        var description = Single<DescriptionAttribute>(sources)?.Description;
        var (nested, isCollection) = depth < MaxDepth ? NestedOf(property.PropertyType, depth + 1) : (null, IsObjectCollection(property.PropertyType));
        return new GmBodyProperty(
            property.Name,
            ClrName(property),
            property.PropertyType,
            getter,
            validations,
            options?.RouteName,
            description,
            nested,
            isCollection,
            RequiresPresence(property, validations));
    }

    /// <summary>
    /// A member whose absence the deserialized value cannot show (review r1 m3, r2 M1): a non-nullable value type that is read
    /// back from the JSON (a setter or a constructor parameter) and either carries <c>[Required]</c> or has no default value.
    /// Computed get-only members and defaulted parameters without <c>[Required]</c> are optional (absent = their own value).
    /// </summary>
    private static bool RequiresPresence(JsonPropertyInfo property, IReadOnlyList<ValidationAttribute> validations) =>
        property.PropertyType.IsValueType
        && Nullable.GetUnderlyingType(property.PropertyType) is null
        && (property.Set is not null || property.AssociatedParameter is not null)
        && (property.AssociatedParameter?.HasDefaultValue != true || validations.OfType<RequiredAttribute>().Any());

    private static T? Single<T>(IEnumerable<ICustomAttributeProvider?> sources)
        where T : Attribute =>
        sources.SelectMany(s => s?.GetCustomAttributes(typeof(T), inherit: true) ?? []).Cast<T>().FirstOrDefault();

    /// <summary>The CLR member name (the validation context's <c>MemberName</c>): the property's, else the JSON name.</summary>
    private static string ClrName(JsonPropertyInfo property) =>
        property.AttributeProvider is MemberInfo member ? member.Name : property.Name;

    /// <summary>The contract of an object property or of the items of an object list; null for anything else.</summary>
    private static (GmBodyContract? Nested, bool IsCollection) NestedOf(Type type, int depth)
    {
        var typeInfo = GameServerJson.Options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
        return typeInfo.Kind switch
        {
            JsonTypeInfoKind.Object => (Build(typeInfo.Type, depth), false),
            JsonTypeInfoKind.Enumerable when typeInfo.ElementType is { } element && IsObject(element) => (Build(element, depth), true),
            _ => (null, false),
        };
    }

    private static bool IsObjectCollection(Type type)
    {
        var typeInfo = GameServerJson.Options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
        return typeInfo.Kind == JsonTypeInfoKind.Enumerable && typeInfo.ElementType is { } element && IsObject(element);
    }

    private static bool IsObject(Type type) =>
        GameServerJson.Options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type).Kind == JsonTypeInfoKind.Object;
}

/// <summary>One JSON property of a <see cref="GmBodyContract"/>.</summary>
internal sealed class GmBodyProperty(
    string jsonName,
    string clrName,
    Type propertyType,
    Func<object, object?> getter,
    IReadOnlyList<ValidationAttribute> validations,
    string? optionsRoute,
    string? description,
    GmBodyContract? nested,
    bool isCollection,
    bool requiresPresence)
{
    public string JsonName { get; } = jsonName;

    public string ClrName { get; } = clrName;

    public Type PropertyType { get; } = propertyType;

    public Func<object, object?> Get { get; } = getter;

    /// <summary>DataAnnotations of the property and of its positional parameter, de-duplicated, in declaration order.</summary>
    public IReadOnlyList<ValidationAttribute> Validations { get; } = validations;

    /// <summary>Route name of <see cref="GmOptionsAttribute"/>, or null.</summary>
    public string? OptionsRoute { get; } = optionsRoute;

    /// <summary><see cref="DescriptionAttribute"/> text: shown in the document, validates nothing.</summary>
    public string? Description { get; } = description;

    /// <summary>Contract of the nested object, or of the items when <see cref="IsCollection"/>; null past the depth limit.</summary>
    public GmBodyContract? Nested { get; } = nested;

    /// <summary>The property is a collection of objects (its items are walked through <see cref="Nested"/>).</summary>
    public bool IsCollection { get; } = isCollection;

    /// <summary>The <c>[Required]</c> attribute of the property, or null.</summary>
    public RequiredAttribute? Required { get; } = validations.OfType<RequiredAttribute>().FirstOrDefault();

    /// <summary>
    /// A non-nullable value type (<c>int</c>, <c>long</c>, <c>bool</c>, enum, …) read back from the JSON without a default value: an
    /// absent member would read as its default, so the server refuses its absence and the document lists it as required, with or
    /// without <c>[Required]</c> (review r1 m3, r2 M1; make it nullable or give its parameter a default to make it optional).
    /// </summary>
    public bool RequiresPresence { get; } = requiresPresence;

    /// <summary>Required in the document and on the server: <c>[Required]</c> or <see cref="RequiresPresence"/>.</summary>
    public bool IsRequired => Required is not null || RequiresPresence;
}
