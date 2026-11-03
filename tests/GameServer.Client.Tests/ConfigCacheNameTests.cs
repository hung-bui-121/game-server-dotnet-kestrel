extern alias sdk;

using Shouldly;
using sdk::GameServer.Client;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 015 T071 U1 (T067 finding): <c>UnityConfigCache.Create</c> checks the cache name with one rule on every platform
/// (<see cref="PersistentConfigCache.FolderNameProblem"/>), never with the running platform's invalid file-name characters.
/// </summary>
public sealed class ConfigCacheNameTests
{
    public static TheoryData<string> Accepted => ["local", "live", "localhost-5080", "dev.eu_1", "a", new string('a', 64)];

    public static TheoryData<string> Refused =>
    [
        string.Empty,
        " ",
        ".",
        "..",
        ".cache",
        "live.",
        "Live",
        "localhost:5080",
        "a/b",
        "a\\b",
        "con",
        "NUL",
        "nul.x",
        "com1",
        "lpt9.log",
        new string('a', 65),
        "caf" + (char)0xE9,
        "live ",
        "a" + (char)1,
    ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public void A_portable_name_is_accepted(string name)
    {
        PersistentConfigCache.FolderNameProblem(name).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_name_outside_the_rule_is_refused_with_the_message(string name)
    {
        PersistentConfigCache.FolderNameProblem(name).ShouldBe(
            $"'{name}' is not a valid config cache name. Fix: use 1 to 64 lowercase letters, digits, '.', '_' or '-', not starting or ending " +
            "with '.' and not a Windows device name (con, nul, com1, " + (char)0x2026 + "), for example 'local', 'live' or 'localhost-5080'.");
    }

    [Fact]
    public void Device_names_are_refused_only_as_the_part_before_the_first_dot()
    {
        PersistentConfigCache.FolderNameProblem("com10").ShouldBeNull();
        PersistentConfigCache.FolderNameProblem("com0").ShouldBeNull();
        PersistentConfigCache.FolderNameProblem("console").ShouldBeNull();
        PersistentConfigCache.FolderNameProblem("live.con").ShouldBeNull();
        PersistentConfigCache.FolderNameProblem("aux.live").ShouldNotBeNull();
    }

    [Fact]
    public void Null_is_an_argument_null_exception()
    {
        Should.Throw<ArgumentNullException>(() => PersistentConfigCache.FolderNameProblem(null!));
    }

    [Fact]
    public void The_package_runtime_never_consults_the_platforms_invalid_file_name_characters()
    {
        var runtime = Path.Combine(RepositoryRoot(), "sdk", "unity", "com.gameserver.client", "Runtime");
        var sources = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories);

        sources.ShouldNotBeEmpty();
        sources.Where(f => File.ReadAllText(f).Contains("GetInvalidFileNameChars", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(runtime, f))
            .ShouldBeEmpty("the cache-name rule is the same on every platform (U1)");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
    }
}
