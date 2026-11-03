using System.Net;
using Fixture.Game;
using GameServer.Protocol.Core.V1;
using GameServer.TestKit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// The game host on its real Local flavor through the kit's <see cref="LocalFlavorFactory{TEntryPoint}"/> (US2-1, SC-002): ready,
/// time, a command round trip — and nothing written outside the factory's temporary root.
/// </summary>
public sealed class FixtureHostSmokeTests
{
    [Fact]
    public async Task The_local_flavor_serves_a_command_and_writes_only_under_the_factory_root()
    {
        string contentRoot;
        string instanceId;
        string playerId;
        await using (var factory = new LocalFlavorFactory<Program>())
        {
            contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            instanceId = factory.InstanceId;
            var client = factory.CreateClient();
            (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);

            var login = await client.LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            playerId = login.PlayerId;
            var player = factory.CreateClient();
            player.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.SessionToken);
            var response = await player.PostProtobufAsync(FixtureModule.SaveRoute, new StringValue { Value = "hello" }, ProtobufHttp.NewRequestId());
            var command = await response.ReadProtobufAsync<CommandResponse>();

            command.Code.ShouldBe("OK");
            command.ConfigVersion.ShouldStartWith("fixture-1+");
            Named(factory.DataFolder, playerId).ShouldNotBeEmpty();
            Named(factory.LogFolder, instanceId).ShouldNotBeEmpty();
        }

        var fixtureRoot = Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        foreach (var folder in new[] { contentRoot, Path.Combine(fixtureRoot, ".local"), AppContext.BaseDirectory })
        {
            Named(folder, instanceId).ShouldBeEmpty();
            Named(folder, playerId).ShouldBeEmpty();
        }

        Directory.Exists(Path.Combine(fixtureRoot, ".local")).ShouldBeFalse();
        foreach (var name in new[] { "data", "analytics", "logs" })
        {
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, name)).ShouldBeFalse();
            Directory.Exists(Path.Combine(contentRoot, name)).ShouldBeFalse();
        }
    }

    /// <summary>Files named with <paramref name="marker"/>, skipping the build output of a project folder.</summary>
    private static List<string> Named(string folder, string marker)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                || (!f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)))
            .Where(f => Path.GetFileName(f).Contains(marker, StringComparison.Ordinal))
            .ToList();
    }
}
