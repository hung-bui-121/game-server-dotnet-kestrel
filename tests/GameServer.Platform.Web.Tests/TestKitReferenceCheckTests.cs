using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Review 013 m8 (FR-010): a production or staging host with <c>GameServer.TestKit</c> loaded fails startup; Development/Test
/// hosts and the kit's own in-process test hosts (TestServer) are not affected. The kit is loaded in this test process.
/// </summary>
public sealed class TestKitReferenceCheckTests
{
    [Theory]
    [InlineData("Staging", "beta")]
    [InlineData("Production", "live")]
    public void A_real_server_outside_development_with_the_kit_loaded_fails(string deploymentEnvironment, string environmentId)
    {
        AppDomain.CurrentDomain.GetAssemblies().ShouldContain(a => a.GetName().Name == "GameServer.TestKit");

        var errors = Run(deploymentEnvironment, environmentId, new KestrelLikeServer());

        errors.ShouldBe([
            $"[deployment] the consumer test kit (GameServer.TestKit) is loaded in '{deploymentEnvironment}' (environment id '{environmentId}'); " +
            "it is for test projects only (test hosts, in-memory stores, test identities). Fix: remove the GameServer.TestKit reference " +
            "from the host project and reference it from the test project only.",
        ]);
    }

    [Theory]
    [InlineData("Development", "local")]
    [InlineData("Test", "test")]
    public void Development_and_test_hosts_may_load_the_kit(string deploymentEnvironment, string environmentId) =>
        Run(deploymentEnvironment, environmentId, new KestrelLikeServer()).ShouldBeEmpty();

    [Fact]
    public async Task The_kits_in_process_test_server_is_exempt_outside_development()
    {
        await using var host = await SampleTestHost.StartAsync();
        var server = host.Services.GetRequiredService<IServer>();

        TestKitReferenceCheck.IsTestServer(server).ShouldBeTrue(server.GetType().FullName);
        Run("Production", "live", server).ShouldBeEmpty();
    }

    private static List<string> Run(string deploymentEnvironment, string environmentId, IServer server)
    {
        var options = new GameServerOptions { GameId = "g", EnvironmentId = environmentId, DeploymentEnvironment = deploymentEnvironment };
        using var services = new ServiceCollection().AddSingleton(server).BuildServiceProvider();
        var result = new StartupCheckResult();
        new TestKitReferenceCheck(options, services).Check(result);
        return result.Errors;
    }

    /// <summary>Any server that is not the in-process TestServer (Kestrel in a deployed host).</summary>
    private sealed class KestrelLikeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
