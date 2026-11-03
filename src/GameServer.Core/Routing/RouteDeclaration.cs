using GameServer.Actors;
using GameServer.Commands;

namespace GameServer.Routing;

/// <summary>Where a route is mapped from (FR-001): decides whether an unmapped declaration is a fault and what Unity gets.</summary>
public enum RouteSurface
{
    /// <summary>Mapped by <c>IPlayerApiModule.MapPlayerApi</c> or the platform when <c>Hosting:MapPlayerApi</c> is on.</summary>
    PlayerApi,

    /// <summary>Mapped by <c>IGmApiModule.MapGmApi</c> or the platform when <c>Hosting:MapGmApi</c> is on. Never generated for Unity.</summary>
    GmApi,

    /// <summary>Mapped by every host (health probes, server time, host-only endpoints).</summary>
    Host,
}

/// <summary>Well-known hosting condition names; modules register their own (Platform.Web <c>AddRouteCondition</c>).</summary>
public static class RouteConditions
{
    /// <summary>Holds where development tools are allowed (Development/Test).</summary>
    public const string DevelopmentTools = "development_tools";
}

/// <summary>
/// One HTTP route as data (FG-026): written once next to the module's commands, read by mapping code, the endpoint check,
/// the request log and code generation. Constructing never validates; the host reports invalid declarations at startup.
/// A reference type on purpose: two identical declarations are two declarations, so a duplicate name is reported.
/// </summary>
public sealed class RouteDeclaration
{
    private const string AnyMethodLabel = "*";

    public RouteDeclaration(
        string name,
        IReadOnlyList<string> methods,
        string template,
        SecurityProfile profile,
        RouteSurface surface,
        string? operation = null,
        string? permission = null,
        string? condition = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(template);
        Name = name;
        Methods = methods
            .Select(method => method.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Template = CanonicalTemplate(template);
        Profile = profile;
        Surface = surface;
        Operation = operation;
        Permission = permission;
        Condition = condition;
    }

    /// <summary>Stable route name, <c>&lt;area&gt;.&lt;route&gt;</c> (rule in PCR platform §20); renaming is a breaking change.</summary>
    public string Name { get; }

    /// <summary>Upper-case HTTP methods, ordinal-sorted; empty = any method.</summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>Route template with exactly one leading <c>/</c>.</summary>
    public string Template { get; }

    public SecurityProfile Profile { get; }

    public RouteSurface Surface { get; }

    /// <summary>Command operation run by the route; null for non-command routes.</summary>
    public string? Operation { get; }

    /// <summary>GM permission (profile Gm only).</summary>
    public string? Permission { get; }

    /// <summary>Hosting condition name; null = mapped whenever its surface is mapped.</summary>
    public string? Condition { get; }

    /// <summary>The methods as one label (<c>GET,HEAD</c>), <c>*</c> for any method.</summary>
    public string MethodLabel => Methods.Count == 0 ? AnyMethodLabel : string.Join(",", Methods);

    public static RouteDeclaration Get(
        string name,
        string template,
        SecurityProfile profile,
        RouteSurface surface = RouteSurface.PlayerApi,
        string? permission = null,
        string? condition = null) =>
        new(name, ["GET"], template, profile, surface, permission: permission, condition: condition);

    public static RouteDeclaration Post(
        string name,
        string template,
        SecurityProfile profile,
        RouteSurface surface = RouteSurface.PlayerApi,
        string? permission = null,
        string? condition = null) =>
        new(name, ["POST"], template, profile, surface, permission: permission, condition: condition);

    public static RouteDeclaration Put(
        string name,
        string template,
        SecurityProfile profile,
        RouteSurface surface = RouteSurface.PlayerApi,
        string? permission = null,
        string? condition = null) =>
        new(name, ["PUT"], template, profile, surface, permission: permission, condition: condition);

    public static RouteDeclaration Delete(
        string name,
        string template,
        SecurityProfile profile,
        RouteSurface surface = RouteSurface.PlayerApi,
        string? permission = null,
        string? condition = null) =>
        new(name, ["DELETE"], template, profile, surface, permission: permission, condition: condition);

    /// <summary>Any HTTP method (e.g. a health probe mapped with <c>MapHealthChecks</c>).</summary>
    public static RouteDeclaration AnyMethod(string name, string template, SecurityProfile profile, RouteSurface surface, string? condition = null) =>
        new(name, [], template, profile, surface, condition: condition);

    /// <summary>
    /// <c>POST {template}</c> running <paramref name="command"/>: name = operation unless given, profile and permission from the
    /// descriptor, surface <see cref="RouteSurface.GmApi"/> for GM commands, else <see cref="RouteSurface.PlayerApi"/>.
    /// </summary>
    public static RouteDeclaration Command(CommandDescriptor command, string template, string? name = null, string? condition = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new RouteDeclaration(
            name ?? command.Operation,
            ["POST"],
            template,
            command.Profile,
            command.Profile == SecurityProfile.Gm ? RouteSurface.GmApi : RouteSurface.PlayerApi,
            command.Operation,
            command.Permission,
            condition);
    }

    /// <summary>The canonical form of a route template: exactly one leading <c>/</c> (research R3).</summary>
    public static string CanonicalTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        // Already canonical (the usual mapped text): no copy, so the request log's catalog lookup does not allocate.
        return template.Length > 0 && template[0] == '/' && (template.Length == 1 || template[1] != '/')
            ? template
            : "/" + template.TrimStart('/');
    }

    /// <summary><c>POST /api/v1/battle/start (battle.start)</c>.</summary>
    public override string ToString() => $"{MethodLabel} {Template} ({Name})";
}
