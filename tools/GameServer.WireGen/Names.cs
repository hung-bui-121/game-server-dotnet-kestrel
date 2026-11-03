using System.Text;

namespace GameServer.WireGen;

/// <summary>
/// Generated C# names (data-model §5, research R13): route names and section ids split on <c>.</c> and <c>_</c>, each part with an
/// upper-case first letter, joined (<c>identity.login_guest</c> → <c>IdentityLoginGuest</c>); template parameters keep their
/// name, C# keywords prefixed with <c>@</c>; protoc's own namespace rule for packages without <c>csharp_namespace</c> (V8).
/// </summary>
internal static class Names
{
    /// <summary>C# reserved keywords (contextual keywords are valid identifiers).</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>PascalCase of a route name or section id.</summary>
    public static string Pascal(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var part in name.Split('.', '_'))
        {
            if (part.Length > 0)
            {
                result.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            }
        }

        return result.ToString();
    }

    /// <summary>A template parameter as a C# parameter name (<c>class</c> → <c>@class</c>); null when it is no identifier.</summary>
    public static string? Parameter(string name) =>
        !HasIdentifierShape(name) ? null : Keywords.Contains(name) ? "@" + name : name;

    /// <summary>ASCII letters, digits and <c>_</c>, not starting with a digit, not a reserved keyword.</summary>
    public static bool IsIdentifier(string value) => HasIdentifierShape(value) && !Keywords.Contains(value);

    private static bool HasIdentifierShape(string value) =>
        value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// protoc's C# namespace of a package without <c>csharp_namespace</c> (<c>UnderscoresToCamelCase(package, true, true)</c>):
    /// <c>fixture.v1</c> → <c>Fixture.V1</c>, <c>my_game.v2beta</c> → <c>MyGame.V2Beta</c> (a letter after <c>_</c>, <c>.</c> or a
    /// digit is upper-cased; other characters are dropped).
    /// </summary>
    public static string ProtocNamespace(string package)
    {
        var result = new StringBuilder(package.Length);
        var capitalizeNext = true;
        foreach (var c in package)
        {
            if (c is >= 'a' and <= 'z')
            {
                result.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                // protoc lower-cases only a leading capital when told not to capitalize; it is told to here.
                result.Append(c);
                capitalizeNext = false;
            }
            else if (c is >= '0' and <= '9')
            {
                result.Append(c);
                capitalizeNext = true;
            }
            else
            {
                capitalizeNext = true;
                if (c == '.')
                {
                    result.Append('.');
                }
            }
        }

        return result.ToString();
    }
}
