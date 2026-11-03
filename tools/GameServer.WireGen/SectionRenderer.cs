using System.Text;

namespace GameServer.WireGen;

/// <summary>
/// The sections file (contracts/generated-files.md §4, FR-052, research R13, N12): per section a typed
/// <c>static readonly SectionKey&lt;T&gt; X</c> and <c>const string XId</c>, then <c>Table</c>; types <c>global::</c>-qualified
/// from the proto model. An untyped section gets <c>XId</c> only and a warning; a section whose message is neither generated
/// by the run nor shipped by the framework fails generation.
/// </summary>
internal static class SectionRenderer
{
    private const string KeyType = "global::GameServer.Client.SectionKey";
    private const string TableType = "global::GameServer.Client.SectionTable";
    private const string Pad = GeneratedCode.Indent + GeneratedCode.Indent;

    /// <summary>The file text; <paramref name="warnings"/> gets one line per untyped section.</summary>
    public static string Render(
        IEnumerable<ContractSection> sections,
        ProtoModel model,
        string ns,
        string className,
        string origin,
        string frameworkVersion,
        ICollection<string> warnings)
    {
        var members = new MemberNames(className);
        members.Add("Table", "[sections] (the section table)");
        var errors = new List<string>();
        var resolved = new List<(ContractSection Section, string Member, ProtoMessage? Message)>();
        foreach (var section in sections.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var member = Names.Pascal(section.Id);
            ProtoMessage? message = null;
            if (section.Message is { } fullName)
            {
                if (model.TryGetClientMessage(fullName, out var found))
                {
                    message = found;
                }
                else
                {
                    errors.Add($"[section {section.Id}] message {fullName} is in neither the listed protos nor the framework protos. Fix: list its " +
                        ".proto in wiregen.json protos, or map the section to a message the client has (FG-028).");
                }

                members.Add(member, $"[section {section.Id}]");
            }

            members.Add(member + "Id", $"[section {section.Id}] (id constant)");
            resolved.Add((section, member, message));
        }

        if (errors.Count > 0)
        {
            throw new WireGenException(errors);
        }

        members.ThrowIfAny();
        var text = new StringBuilder();
        GeneratedCode.AppendHeader(text, frameworkVersion);
        text.Append("namespace ").Append(ns).Append("\n{\n");
        text.Append(GeneratedCode.Indent).Append("/// <summary>Client-visible sections of ").Append(GeneratedCode.Of(origin))
            .Append(": id constants, typed keys and the id → parser table.</summary>\n");
        text.Append(GeneratedCode.Indent).Append("public static class ").Append(className).Append('\n');
        text.Append(GeneratedCode.Indent).Append("{\n");
        foreach (var (section, member, message) in resolved)
        {
            text.Append(Pad).Append("/// <summary>Section <c>").Append(GeneratedCode.Xml(section.Id)).Append("</c> (module <c>")
                .Append(GeneratedCode.Xml(section.Owner)).Append("</c>)");
            if (message is null)
            {
                warnings.Add($"warning: [section {section.Id}] is registered without its message type: only {className}.{member}Id is generated. " +
                    "Fix: AddAggregateWireMapper<TState, TWire>(…) on the server (FG-028).");
                text.Append(" (registered without its message type: no parser).</summary>\n");
            }
            else
            {
                var type = "global::" + message.CSharpName;
                text.Append(", message <c>").Append(GeneratedCode.Xml(message.FullName)).Append("</c>.</summary>\n");
                text.Append(Pad).Append("public static readonly ").Append(KeyType).Append('<').Append(type).Append("> ").Append(member).Append(" =\n");
                text.Append(Pad).Append(GeneratedCode.Indent).Append("new ").Append(KeyType).Append('<').Append(type).Append(">(")
                    .Append(member).Append("Id, ").Append(type).Append(".Parser);\n\n");
            }

            text.Append(Pad).Append("public const string ").Append(member).Append("Id = ").Append(GeneratedCode.Literal(section.Id)).Append(";\n\n");
        }

        var typed = resolved.Where(r => r.Message is not null).Select(r => r.Member);
        text.Append(Pad).Append("/// <summary>Every typed section above (register a game's own table with SectionStore.Register).</summary>\n");
        text.Append(Pad).Append("public static readonly ").Append(TableType).Append(" Table = new ").Append(TableType).Append('(')
            .Append(string.Join(", ", typed)).Append(");\n");
        text.Append(GeneratedCode.Indent).Append("}\n}\n");
        return text.ToString();
    }
}
