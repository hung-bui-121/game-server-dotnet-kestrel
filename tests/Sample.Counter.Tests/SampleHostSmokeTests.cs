using System.Net;
using GameServer.Platform.Web.Auth;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Tests;

/// <summary>The shipped sample host boots with its own config and serves the quickstart flow.</summary>
public sealed class SampleHostSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SampleHostSmokeTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("GameServer:Outbox:SweeperEnabled", "false"));

    [Fact]
    public async Task Sample_host_serves_time_command_and_snapshot()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, "smoke");
        var command = await (await client.PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 2 }, ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>();
        command.Code.ShouldBe("OK");
        command.ConfigVersion.ShouldStartWith("sample-1+");

        var snapshot = await (await client.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();
        snapshot.Sections.Single(s => s.Id == "counter").Rev.ShouldBe(1);
    }
}
