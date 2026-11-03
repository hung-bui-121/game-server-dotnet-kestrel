namespace GameServer.WireGen;

/// <summary>A piece of a route template: literal text or a <c>{name}</c> parameter.</summary>
internal sealed record TemplatePart(string Text, bool IsParameter);

/// <summary>
/// Route templates the generated route code can express (research R13): literal segments and plain <c>{name}</c> parameters,
/// also inside a segment (<c>{id}.json</c>). A constraint (<c>:</c>), an optional parameter (<c>?</c>), a default (<c>=</c>), a
/// catch-all (<c>*</c>) or an escaped brace fail generation naming the route.
/// </summary>
internal static class TemplateParser
{
    /// <summary>The parts of <paramref name="template"/> without its leading <c>/</c> (relative to the SDK base URL).</summary>
    public static IReadOnlyList<TemplatePart> Parse(string routeName, string template)
    {
        var path = template.TrimStart('/');
        var parts = new List<TemplatePart>();
        var literal = new System.Text.StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '}')
            {
                throw Unsupported(routeName, template, "a '}' outside a parameter");
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = path.IndexOf('}', i + 1);
            if (end < 0 || (i + 1 < path.Length && path[i + 1] == '{'))
            {
                throw Unsupported(routeName, template, "an escaped or unclosed '{'");
            }

            var name = path[(i + 1)..end];
            if (name.IndexOfAny([':', '?', '=', '*']) is var at and >= 0)
            {
                throw Unsupported(routeName, template, name[at] switch
                {
                    ':' => "a constraint ':'",
                    '?' => "an optional parameter '?'",
                    '=' => "a default value '='",
                    _ => "a catch-all '*'",
                });
            }

            if (name.Length == 0)
            {
                throw Unsupported(routeName, template, "an empty parameter '{}'");
            }

            if (literal.Length > 0)
            {
                parts.Add(new TemplatePart(literal.ToString(), IsParameter: false));
                literal.Clear();
            }

            parts.Add(new TemplatePart(name, IsParameter: true));
            i = end;
        }

        if (literal.Length > 0)
        {
            parts.Add(new TemplatePart(literal.ToString(), IsParameter: false));
        }

        return parts;
    }

    private static WireGenException Unsupported(string routeName, string template, string what) =>
        new($"[route {routeName}] template '{template}' uses {what}: the generated Unity route code cannot express it. Fix: declare the " +
            "player-facing route with plain {name} parameters (FG-026).");
}
