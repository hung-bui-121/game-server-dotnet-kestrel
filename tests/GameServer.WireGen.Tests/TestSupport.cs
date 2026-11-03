using System.Text;

namespace GameServer.WireGen.Tests;

/// <summary>A temporary folder deleted on dispose, with helpers to write files and snapshot the tree.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Directory.CreateTempSubdirectory("wiregen-test-").FullName;
    }

    public string Root { get; }

    public string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));

    public string Write(string relative, string text)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public string Folder(string relative) => Directory.CreateDirectory(Path(relative)).FullName;

    /// <summary>Every file and folder under the root with its bytes and write time: equal snapshots = nothing created or changed.</summary>
    public string Snapshot() => string.Join(
        "\n",
        Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(entry => File.Exists(entry)
                ? $"{entry} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry)))} {File.GetLastWriteTimeUtc(entry).Ticks}"
                : entry + "/"));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>Framework paths and test inputs shared by the tool tests.</summary>
internal static class Repo
{
    public static string Root => ToolInfo.Current.FrameworkRoot;

    public static string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));

    public static string TestData(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>Build configuration of this test run (<c>bin/&lt;configuration&gt;/&lt;tfm&gt;/</c>), used to run sibling projects with <c>--no-build</c>.</summary>
    public static string Configuration => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar)).Parent!.Name;

    /// <summary>The generator's own pins: the tests run against the real framework versions.</summary>
    public static ToolInfo Tool => ToolInfo.Current;

    /// <summary>A config JSON (format gameserver.wiregen/1) with the given body properties.</summary>
    public static string Config(string body) => "{\n  \"format\": \"gameserver.wiregen/1\",\n" + body + "\n}\n";

    /// <summary>Forward-slash form for JSON strings.</summary>
    public static string Json(string path) => path.Replace('\\', '/');
}
