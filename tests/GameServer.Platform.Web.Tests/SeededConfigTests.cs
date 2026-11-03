using GameServer.Config;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>Slice 015 T014: the seeded copy of the sample test config starts a host like the original and reports its faults.</summary>
public sealed class SeededConfigTests
{
    [Fact]
    public async Task An_unchanged_copy_starts_with_the_same_config_version()
    {
        using var seeded = new SeededConfig();
        await using var original = await SampleTestHost.StartAsync();
        await using var copy = await SampleTestHost.StartAsync(settings: seeded.Apply);

        copy.Services.GetRequiredService<IGameConfig>().ConfigVersion.ShouldBe(original.Services.GetRequiredService<IGameConfig>().ConfigVersion);
    }

    [Fact]
    public async Task A_removed_file_fails_with_the_loader_line()
    {
        using var seeded = new SeededConfig().Without("battle.json");

        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(settings: seeded.Apply);
        });

        ex.Violations.ShouldContain(
            $"[config] file '{Path.Combine(seeded.BasePath, "battle.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.");
    }

    [Fact]
    public void The_folder_is_gone_after_dispose()
    {
        var seeded = new SeededConfig().With("extra.json", "{}").Manifest(m => m);
        var path = seeded.Path;
        File.Exists(Path.Combine(seeded.BasePath, "extra.json")).ShouldBeTrue();

        seeded.Dispose();

        Directory.Exists(path).ShouldBeFalse();
    }
}
