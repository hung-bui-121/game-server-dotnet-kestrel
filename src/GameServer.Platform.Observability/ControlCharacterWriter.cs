using System.Text;

namespace GameServer.Platform.Observability;

/// <summary>
/// Console sink output filter (FR-087, LOG-05): writes to the inner writer with every control character (<see cref="char.IsControl(char)"/>:
/// C0, DEL, C1 — ESC, CR, LF, TAB included) replaced by <c>?</c>, the rule of the request event's <c>Route</c> cleaning, without
/// truncation. Rendered values cannot start terminal escape sequences or begin a fake log line.
/// </summary>
internal sealed class ControlCharacterWriter(TextWriter inner) : TextWriter(inner.FormatProvider)
{
    private const char Replacement = '?';

    public override Encoding Encoding => inner.Encoding;

    /// <summary>
    /// <paramref name="text"/> (an exception's <c>ToString()</c>) cleaned like the writer, except its line breaks: <c>\n</c> and
    /// <c>\r\n</c> stay; a lone <c>\r</c> (it would overwrite the visible line) becomes <c>?</c>.
    /// </summary>
    public static void WriteKeepingLineBreaks(string text, TextWriter output)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lineBreak = c == '\n' || (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n');
            output.Write(lineBreak || !char.IsControl(c) ? c : Replacement);
        }
    }

    public override void Write(char value) => inner.Write(char.IsControl(value) ? Replacement : value);

    public override void Write(string? value) => Write(value.AsSpan());

    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        if (!buffer.ContainsAnyInRange('\0', '\u001f') && !buffer.ContainsAnyInRange('\u007f', '\u009f'))
        {
            inner.Write(buffer);
            return;
        }

        foreach (var c in buffer)
        {
            Write(c);
        }
    }

    public override void Flush() => inner.Flush();
}
