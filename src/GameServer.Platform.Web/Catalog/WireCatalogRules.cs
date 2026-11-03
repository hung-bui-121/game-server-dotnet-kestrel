using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Validation;
using GameServer.Routing;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Routing.Patterns;

namespace GameServer.Platform.Web.Catalog;

/// <summary>
/// Declaration rules of the catalog (FR-006, FR-007, FR-054, FR-060; research R3, R5): one report line per fault, texts of
/// data-model §2.1. Run by <c>UseGameServer</c>, <c>--provision</c> and the export mode; never throws.
/// </summary>
internal static partial class WireCatalogRules
{
    public const int MaxNameLength = 64;

    /// <summary>Field number of the value in a proto map entry message (key = 1, value = 2).</summary>
    private const int MapValueField = 2;

    public static IReadOnlyList<string> Validate(WireCatalog catalog)
    {
        var errors = new List<string>();
        foreach (var entry in catalog.Routes)
        {
            ValidateRoute(entry, catalog.ConditionNames, errors);
        }

        ValidateDuplicateNames(catalog.Routes, errors);
        ValidateDuplicateMethods(catalog.Routes, errors);
        ValidatePublicEndpoints(catalog, errors);
        foreach (var duplicate in catalog.DuplicateMappers)
        {
            errors.Add(
                $"[aggregate {duplicate.AggregateId}] has {duplicate.Count} wire mappers. Fix: register one mapper per aggregate; replace a framework " +
                "mapper with ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).");
        }

        foreach (var (id, contract) in catalog.Contracts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ValidateContract(id, contract, errors);
        }

        return errors;
    }

    /// <summary>Warnings that do not stop the host: sections whose mapper was registered without its message type (FG-028).</summary>
    public static IReadOnlyList<string> Warnings(WireCatalog catalog) =>
        catalog.Sections
            .Where(section => section.MessageFullName is null)
            .Select(section =>
                $"[aggregate {section.Id}] wire mapper registered without its message type: the catalog has no message for this section and " +
                "Unity gets no parser. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).")
            .ToArray();

    private static void ValidateRoute(RouteEntry entry, IReadOnlySet<string> conditions, List<string> errors)
    {
        var route = entry.Declaration;
        if (route.Name.Length > MaxNameLength || !NamePattern().IsMatch(route.Name))
        {
            errors.Add(
                $"[route {route.Name}] (owner {entry.Owner}) name does not match ^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$ (≤ {MaxNameLength}). " +
                "Fix: use <area>.<route> in lower case (FG-026).");
        }

        if (ParseError(route.Template) is { } parseError)
        {
            errors.Add(
                $"[route {route.Name}] template '{route.Template}' is not a valid route template: {parseError}. Fix: correct the declaration (FG-026).");
        }
        else if (!VersionSegment().IsMatch(route.Template) && !EndpointSecurityValidator.UnversionedProbeRoutes.Contains(route.Template))
        {
            errors.Add($"[route {route.Name}] template '{route.Template}' has no version segment. Fix: prefix with /v{{n}}/ (API-02).");
        }

        if ((route.Profile == SecurityProfile.Gm) == string.IsNullOrEmpty(route.Permission))
        {
            errors.Add(
                $"[route {route.Name}] has profile {route.Profile} and permission '{route.Permission}'. " +
                "Fix: GM routes declare their permission, other routes none (SEC-07).");
        }

        if (route.Condition is { } condition && !conditions.Contains(condition))
        {
            errors.Add(
                $"[route {route.Name}] uses hosting condition '{condition}' which is not registered. " +
                $"Fix: services.AddRouteCondition(\"{condition}\", …) in the module (FG-026).");
        }
    }

    private static string? ParseError(string template)
    {
        try
        {
            RoutePatternFactory.Parse(template);
            return null;
        }
        catch (RoutePatternException ex)
        {
            return ex.Message;
        }
    }

    private static void ValidateDuplicateNames(IReadOnlyList<RouteEntry> routes, List<string> errors)
    {
        foreach (var group in routes.GroupBy(r => r.Declaration.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var first = group.First();
            foreach (var other in group.Skip(1))
            {
                errors.Add($"[route {group.Key}] is declared by {first.Owner} and {other.Owner}. Fix: give each route a unique name (FG-026).");
            }
        }
    }

    /// <summary>One line per pair of declarations sharing a template and at least one method; any-method overlaps every method.</summary>
    private static void ValidateDuplicateMethods(IReadOnlyList<RouteEntry> routes, List<string> errors)
    {
        foreach (var group in routes.GroupBy(r => r.Declaration.Template, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var sameTemplate = group.ToArray();
            for (var i = 0; i < sameTemplate.Length; i++)
            {
                for (var j = i + 1; j < sameTemplate.Length; j++)
                {
                    var (a, b) = (sameTemplate[i], sameTemplate[j]);
                    if (Overlap(a.Declaration, b.Declaration) is { } methods)
                    {
                        errors.Add(
                            $"[route {methods} {group.Key}] is declared twice: {a.Declaration.Name} ({a.Owner}) and {b.Declaration.Name} ({b.Owner}). " +
                            "Fix: declare it once (ARC-07).");
                    }
                }
            }
        }
    }

    /// <summary>The methods both declarations map, as a label; null when they share none.</summary>
    private static string? Overlap(RouteDeclaration a, RouteDeclaration b)
    {
        if (a.Methods.Count == 0)
        {
            return b.MethodLabel;
        }

        if (b.Methods.Count == 0)
        {
            return a.MethodLabel;
        }

        var shared = a.Methods.Intersect(b.Methods, StringComparer.Ordinal).ToArray();
        return shared.Length == 0 ? null : string.Join(",", shared);
    }

    /// <summary>BC-5: a template a module lists in <c>PublicEndpoints</c> must not be declared with another profile.</summary>
    private static void ValidatePublicEndpoints(WireCatalog catalog, List<string> errors)
    {
        foreach (var listed in catalog.ModulePublicEndpoints)
        {
            foreach (var entry in catalog.RoutesAt(listed.Template).Where(r => r.Declaration.Profile != SecurityProfile.Public))
            {
                errors.Add(
                    $"[module {listed.Module}] lists {listed.Template} in PublicEndpoints but route {entry.Declaration.Name} declares profile " +
                    $"{entry.Declaration.Profile}. Fix: declare it Public or remove it from PublicEndpoints (SEC-09).");
            }
        }
    }

    private static void ValidateContract(string id, IAggregateWireContract contract, List<string> errors)
    {
        var members = StateMembers.Of(contract.StateType);
        foreach (var path in contract.HiddenMembers.Where(path => !members.Resolve(path, out _)))
        {
            errors.Add(
                $"[aggregate {id}] hidden member '{path}' does not exist on {contract.StateType.Name}. Fix: use C# member names joined by '.' (FG-027).");
        }

        foreach (var path in contract.MayStayDefaultFields.Where(path => !ResolvesField(contract.Message, path)))
        {
            errors.Add($"[aggregate {id}] field '{path}' does not exist on {contract.Message.FullName}. Fix: use proto field names joined by '.' (FG-027).");
        }
    }

    /// <summary>Proto field names joined by '.'; repeated and map fields are transparent (a map descends into its value message).</summary>
    private static bool ResolvesField(MessageDescriptor message, string path)
    {
        MessageDescriptor? current = message;
        foreach (var segment in path.Split('.'))
        {
            var field = current?.FindFieldByName(segment);
            if (field is null)
            {
                return false;
            }

            current = MessageOf(field.IsMap ? field.MessageType.FindFieldByNumber(MapValueField) : field);
        }

        return true;
    }

    private static MessageDescriptor? MessageOf(FieldDescriptor field) => field.FieldType == FieldType.Message ? field.MessageType : null;

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("(^|/)v[0-9]+(/|$)")]
    private static partial Regex VersionSegment();
}
