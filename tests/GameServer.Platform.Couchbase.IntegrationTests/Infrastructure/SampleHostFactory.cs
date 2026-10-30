using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// The shipped sample host in process (<see cref="WebApplicationFactory{TEntryPoint}"/>, content root = the SampleHost source
/// folder, so <c>appsettings.{Flavor}.json</c> is the committed flavor file) with deployment settings layered on top. Analytics
/// files are off unless a test turns them on with its own directory and instance id.
/// </summary>
internal static class SampleHostFactory
{
    public static WebApplicationFactory<Program> Create(string flavor, IReadOnlyDictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(flavor);
            b.UseSetting("GameServer:Hosting:DrainDelaySeconds", "0");

            // Slice 012: the flavor files enable analytics files under the content root (the source folder) with the machine
            // name as instance id; these tests do not need them (settings below may turn them back on).
            b.UseSetting("GameServer:Analytics:Enabled", "false");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
}
