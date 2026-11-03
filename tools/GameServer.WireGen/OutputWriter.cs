using System.Text;

namespace GameServer.WireGen;

/// <summary>A generated file: path relative to <c>output.directory</c> (<c>/</c>-separated) and its normalized bytes.</summary>
internal sealed record OutputFile(string RelativePath, byte[] Bytes);

/// <summary>What one write or verify found, as lines for the summary (paths relative to the output root).</summary>
internal sealed record OutputReport(IReadOnlyList<string> Written, IReadOnlyList<string> Created, IReadOnlyList<string> Differences, int Unchanged);

/// <summary>
/// Steps 10–11 of contracts/wiregen-cli.md §4 (FR-023, FR-026, FR-027, research R10): every file normalized to the selected line
/// ending and UTF-8 without BOM; <c>generate</c> compares bytes with disk and writes only changed files (temp file in the same
/// folder, then replace), creates subfolders only inside <c>output.directory</c>, never deletes and never touches a
/// <c>.meta</c>; <c>verify</c> writes nothing and lists each differing or missing file.
/// </summary>
internal static class OutputWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static OutputFile Normalize(string relativePath, string text, LineEnding lineEnding)
    {
        var lf = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        var normalized = lineEnding == LineEnding.Crlf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
        return new OutputFile(relativePath.Replace('\\', '/'), Utf8NoBom.GetBytes(normalized));
    }

    public static OutputReport Generate(string outputRoot, string outputDirectory, IReadOnlyList<OutputFile> files)
    {
        var written = new List<string>();
        var created = new List<string>();
        var unchanged = 0;
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var path = FullPath(outputDirectory, file);
            var exists = File.Exists(path);
            if (exists && File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Bytes))
            {
                unchanged++;
                continue;
            }

            var folder = Path.GetDirectoryName(path)!;
            try
            {
                Directory.CreateDirectory(folder);
                var temp = Path.Combine(folder, "." + Path.GetFileName(path) + ".wiregen-tmp");
                File.WriteAllBytes(temp, file.Bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WireGenException($"[output] {path} cannot be written: {ex.Message} Fix: check the folder and close programs holding the file.");
            }

            var shown = Shown(outputRoot, path);
            written.Add(shown);
            if (!exists)
            {
                created.Add(shown);
            }
        }

        return new OutputReport(written, created, [], unchanged);
    }

    public static OutputReport Verify(string outputRoot, string outputDirectory, IReadOnlyList<OutputFile> files)
    {
        var differences = new List<string>();
        var unchanged = 0;
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var path = FullPath(outputDirectory, file);
            if (!File.Exists(path))
            {
                differences.Add($"missing: {Shown(outputRoot, path)}");
            }
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Bytes))
            {
                differences.Add($"differs: {Shown(outputRoot, path)}");
            }
            else
            {
                unchanged++;
            }
        }

        return new OutputReport([], [], differences, unchanged);
    }

    private static string FullPath(string outputDirectory, OutputFile file) => Path.GetFullPath(Path.Combine(outputDirectory, file.RelativePath));

    private static string Shown(string outputRoot, string path) => Path.GetRelativePath(outputRoot, path).Replace('\\', '/');
}
