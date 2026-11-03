using System.Text.Json.Nodes;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 015 T071 G3: under <see cref="LocalFlavorFactory{TEntryPoint}"/> a startup failure throws the
/// <see cref="GameServerStartupException"/> with the report as its violations instead of printing it and exiting 1, and the factory
/// still disposes cleanly. The real host keeps stderr + exit 1 (<c>LateEndpointValidationTests</c>). Console.Error is process-wide,
/// so this class runs alone.
/// </summary>
[Collection(LocalFlavorFactoryConsoleCollection.Name)]
public sealed class LocalFlavorFactoryStartupFailureTests
{
    private const string MissingBattleLine =
        "[module sample.game] required config file 'battle.json' is missing. Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01).";

    [Fact]
    public async Task A_report_from_use_game_server_is_thrown_by_start_server_and_nothing_is_printed()
    {
        using var seeded = new SeededConfig().Without("battle.json").Manifest(RemoveBattle);
        var factory = new LocalFlavorFactory<Program>(new LocalFlavorOptions { Settings = { [SeededConfig.ConfigPathKey] = seeded.Path } });
        var original = Console.Error;
        await using var error = new StringWriter();
        Console.SetError(TextWriter.Synchronized(error));
        GameServerStartupException ex;
        try
        {
            ex = Should.Throw<GameServerStartupException>(factory.StartServer);
        }
        finally
        {
            Console.SetError(original);
        }

        ex.Violations.ShouldBe([MissingBattleLine]);
        error.ToString().ShouldBeEmpty();
        await Should.NotThrowAsync(async () => await factory.DisposeAsync());
        Directory.Exists(factory.Root).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_first_use_member_throws_the_report()
    {
        using var seeded = new SeededConfig().Without("battle.json").Manifest(RemoveBattle);
        await using var factory = new LocalFlavorFactory<Program>(new LocalFlavorOptions { Settings = { [SeededConfig.ConfigPathKey] = seeded.Path } });

        Should.Throw<GameServerStartupException>(() => factory.CreateClient()).Violations.ShouldBe([MissingBattleLine]);
    }

    private static JsonObject RemoveBattle(JsonObject manifest)
    {
        foreach (var list in new[] { "files", "clientVisible" })
        {
            var names = manifest[list]!.AsArray();
            names.Remove(names.SingleOrDefault(f => f!.GetValue<string>() == "battle.json"));
        }

        return manifest;
    }
}

/// <summary>Tests that redirect <see cref="Console.Error"/> around a <see cref="LocalFlavorFactory{TEntryPoint}"/> run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalFlavorFactoryConsoleCollection
{
    public const string Name = "LocalFlavorFactory console capture";
}
