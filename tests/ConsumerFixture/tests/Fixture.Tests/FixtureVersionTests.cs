using System.Text.Json;
using GameServer.Platform;
using GameServer.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// The versions a game sees (FR-050, FR-052, FR-054, SC-006): the framework version through its project references, and its own
/// host version (<c>&lt;Version&gt;1.2.3&lt;/Version&gt;</c> of <c>Fixture.Host</c>) next to it in the runtime info and the startup line.
/// </summary>
public sealed class FixtureVersionTests
{
    /// <summary>The framework version this repository pins (a game keeps it equal to the tag of its framework submodule).</summary>
    private const string PinnedFrameworkVersion = "0.7.0";

    private static readonly TimeSpan LogWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_framework_version_reaches_the_game_through_its_project_references()
    {
        GameServerVersion.Framework.ShouldBe(PinnedFrameworkVersion);
        GameServerVersion.FrameworkInformational.ShouldStartWith(PinnedFrameworkVersion);
    }

    [Fact]
    public async Task The_host_reports_its_own_version_and_the_framework_version_at_startup()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        var runtime = factory.Services.GetRequiredService<GameServerRuntimeInfo>();

        runtime.GameAssembly.ShouldBe("Fixture.Host");
        runtime.GameVersion.ShouldStartWith("1.2.3");
        runtime.FrameworkVersion.ShouldBe(GameServerVersion.FrameworkInformational);
        runtime.InstanceId.ShouldBe(factory.InstanceId);

        var line = await StartupLineAsync(factory.LogFolder);
        line.GetProperty("GameId").GetString().ShouldBe("fixture");
        line.GetProperty("GameVersion").GetString().ShouldBe(runtime.GameVersion);
        line.GetProperty("FrameworkVersion").GetString().ShouldBe(runtime.FrameworkVersion);
        line.GetProperty("EnvironmentId").GetString().ShouldBe("local");
        line.GetProperty("InstanceId").GetString().ShouldBe(factory.InstanceId);
    }

    /// <summary>The startup event of the host's compact JSON log file (written through an asynchronous queue, hence the wait).</summary>
    private static async Task<JsonElement> StartupLineAsync(string logFolder)
    {
        var deadline = DateTimeOffset.UtcNow + LogWait;
        while (true)
        {
            foreach (var line in Directory.Exists(logFolder) ? Directory.GetFiles(logFolder).SelectMany(ReadLines) : [])
            {
                if (Parse(line) is { } entry
                    && entry.TryGetProperty("@mt", out var template)
                    && template.GetString()!.StartsWith("Starting {GameId}", StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ShouldAssertException($"No startup line in {logFolder} within {LogWait}.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Null for the last line while the sink is still writing it.</summary>
    private static JsonElement? Parse(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
