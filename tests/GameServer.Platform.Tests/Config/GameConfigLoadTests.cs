using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>FR-001 "once per start", FR-032 groundwork (slice 015 T013): one loader run per host, marked failures.</summary>
public sealed class GameConfigLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-configload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_loader_runs_once_across_concurrent_resolves()
    {
        Write("base/manifest.json", """{"label":"v1","files":["a.json"],"clientVisible":[]}""");
        Write("base/a.json", """{"x":1}""");
        var load = new GameConfigLoad(_root, "Development");

        var configs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Enumerable.Range(0, 10).Select(_ => load.GetOrThrow()).ToArray())));

        load.LoadCount.ShouldBe(1);
        configs.SelectMany(c => c).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void A_failing_load_runs_once_and_each_resolve_throws_a_new_marked_exception_with_the_same_violations()
    {
        Write("base/manifest.json", """{"label":"v1","files":["missing.json"],"clientVisible":[]}""");
        var load = new GameConfigLoad(_root, "Development");

        var first = Should.Throw<GameServerStartupException>(load.GetOrThrow);
        var second = Should.Throw<GameServerStartupException>(load.GetOrThrow);

        load.LoadCount.ShouldBe(1);
        second.ShouldNotBeSameAs(first);
        second.Violations.ShouldBe(first.Violations);
        first.Violations.Single().ShouldStartWith("[config] file '");
        Unavailable.Find(first).ShouldBe(Unavailable.GameConfig);
        Unavailable.Find(second).ShouldBe(Unavailable.GameConfig);
    }

    [Fact]
    public void Not_loaded_mode_never_touches_the_disk_and_gives_the_provision_line()
    {
        var load = new GameConfigLoad(Path.Combine(_root, "does-not-exist"), "Development") { Mode = GameConfigLoadMode.NotLoaded };

        var ex = Should.Throw<GameServerStartupException>(load.GetOrThrow);

        load.LoadCount.ShouldBe(0);
        ex.Violations.ShouldBe(["[config] the game config is not loaded in provision mode. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023)."]);
        Unavailable.Find(ex).ShouldBe(Unavailable.Provision);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
