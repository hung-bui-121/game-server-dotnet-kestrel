using System.Text;

namespace GameServer.WireGen;

/// <summary>
/// The routes file (contracts/generated-files.md §3, FR-032, research R13): per route a <c>const string X</c> (no leading slash)
/// or a <c>static string X(params)</c> helper escaping each parameter with <c>Uri.EscapeDataString</c> in template order, then
/// <c>const string XMethod</c> (<c>*</c> = any method). Members ordered ordinally by route name; deterministic for any input order.
/// </summary>
internal static class RouteRenderer
{
    private const string Escape = "global::System.Uri.EscapeDataString";

    public static string Render(IEnumerable<ContractRoute> routes, string ns, string className, string origin, string frameworkVersion)
    {
        var members = new MemberNames(className);
        var parsed = new List<(ContractRoute Route, string Member, IReadOnlyList<TemplatePart> Parts)>();
        foreach (var route in routes.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var member = Names.Pascal(route.Name);
            members.Add(member, $"[route {route.Name}]");
            members.Add(member + "Method", $"[route {route.Name}] (method constant)");
            parsed.Add((route, member, TemplateParser.Parse(route.Name, route.Template)));
        }

        members.ThrowIfAny();
        var text = new StringBuilder();
        GeneratedCode.AppendHeader(text, frameworkVersion);
        text.Append("namespace ").Append(ns).Append("\n{\n");
        text.Append(GeneratedCode.Indent).Append("/// <summary>Player-facing routes of ").Append(GeneratedCode.Of(origin))
            .Append(", relative to the SDK base URL (no leading slash).</summary>\n");
        text.Append(GeneratedCode.Indent).Append("public static class ").Append(className).Append('\n');
        text.Append(GeneratedCode.Indent).Append("{\n");
        for (var i = 0; i < parsed.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\n');
            }

            AppendRoute(text, parsed[i].Route, parsed[i].Member, parsed[i].Parts);
        }

        text.Append(GeneratedCode.Indent).Append("}\n}\n");
        return text.ToString();
    }

    private static void AppendRoute(StringBuilder text, ContractRoute route, string member, IReadOnlyList<TemplatePart> parts)
    {
        const string pad = GeneratedCode.Indent + GeneratedCode.Indent;
        var method = route.Methods.Count == 0 ? "*" : route.Methods[0];
        text.Append(pad).Append("/// <summary>").Append(Summary(route, method)).Append("</summary>\n");
        var parameters = parts.Where(p => p.IsParameter).Select(p => Parameter(route, p.Text)).ToArray();
        if (parameters.Length == 0)
        {
            text.Append(pad).Append("public const string ").Append(member).Append(" = ").Append(GeneratedCode.Literal(route.Template.TrimStart('/'))).Append(";\n");
        }
        else
        {
            text.Append(pad).Append("public static string ").Append(member).Append('(')
                .Append(string.Join(", ", parameters.Select(p => "string " + p))).Append(") =>\n");
            var pieces = parts.Select(p => p.IsParameter ? $"{Escape}({Parameter(route, p.Text)})" : GeneratedCode.Literal(p.Text));
            text.Append(pad).Append(GeneratedCode.Indent).Append(string.Join(" + ", pieces)).Append(";\n");
        }

        text.Append('\n');
        text.Append(pad).Append("/// <summary>HTTP method of <see cref=\"").Append(member).Append("\"/>")
            .Append(method == "*" ? " (<c>*</c> = any)" : string.Empty).Append(".</summary>\n");
        text.Append(pad).Append("public const string ").Append(member).Append("Method = ").Append(GeneratedCode.Literal(method)).Append(";\n");
    }

    private static string Summary(ContractRoute route, string method)
    {
        var summary = new StringBuilder();
        summary.Append("<c>").Append(method).Append(' ').Append(GeneratedCode.Xml(route.Template.TrimStart('/'))).Append("</c>: ");
        if (route.Operation is { } operation)
        {
            summary.Append("command <c>").Append(GeneratedCode.Xml(operation)).Append("</c>, ");
        }

        summary.Append("profile ").Append(route.Profile);
        if (route.Profile == "Dev")
        {
            summary.Append(" (Development/Test servers only)");
        }

        summary.Append(route.Owner switch
        {
            "platform" => ", platform",
            "host" => ", host",
            var module => $", module <c>{GeneratedCode.Xml(module)}</c>",
        });
        summary.Append(method == "*" ? "; any HTTP method." : ".");
        return summary.ToString();
    }

    private static string Parameter(ContractRoute route, string name) =>
        Names.Parameter(name) ?? throw new WireGenException(
            $"[route {route.Name}] template parameter '{name}' is not a C# identifier. Fix: use letters, digits and '_' in the parameter name (FG-026).");
}
