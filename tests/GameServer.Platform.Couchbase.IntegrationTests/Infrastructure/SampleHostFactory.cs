using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// The shipped sample host in process (<see cref="WebApplicationFactory{TEntryPoint}"/>, content root = the SampleHost source
/// folder, so <c>appsettings.{Flavor}.json</c> is the committed flavor file) with deployment settings layered on top.
/// </summary>
internal static class SampleHostFactory
{
    public static WebApplicationFactory<Program> Create(string flavor, IReadOnlyDictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(flavor);
            b.UseSetting("GameServer:Hosting:DrainDelaySeconds", "0");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
}
