using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// The consumer test kit (<c>GameServer.TestKit</c>: test hosts, in-memory stores, test identities) belongs in test projects
/// only (FR-010, review 013 m8). Outside Development/Test a host with the kit loaded fails startup, unless the host runs on the
/// in-process test server (<c>TestServer</c>: the kit's own <c>TestHost</c> and <c>LocalFlavorFactory</c>, which test the
/// Staging and Production rules). Only a loaded assembly is seen: a reference whose types the host never uses is not.
/// </summary>
internal sealed class TestKitReferenceCheck(GameServerOptions options, IServiceProvider services) : IStartupCheck
{
    internal const string TestKitAssembly = "GameServer.TestKit";

    /// <summary>Full name of the in-process test server (Microsoft.AspNetCore.TestHost; compared by name, not referenced).</summary>
    internal const string TestServerType = "Microsoft.AspNetCore.TestHost.TestServer";

    public void Check(StartupCheckResult result)
    {
        if (options.AllowsDevelopmentTools || !IsLoaded(TestKitAssembly) || IsTestServer(services.GetService<IServer>()))
        {
            return;
        }

        result.Errors.Add(
            $"[deployment] the consumer test kit ({TestKitAssembly}) is loaded in '{options.DeploymentEnvironment}' (environment id " +
            $"'{options.EnvironmentId}'); it is for test projects only (test hosts, in-memory stores, test identities). Fix: remove the " +
            $"{TestKitAssembly} reference from the host project and reference it from the test project only.");
    }

    internal static bool IsTestServer(IServer? server) => string.Equals(server?.GetType().FullName, TestServerType, StringComparison.Ordinal);

    private static bool IsLoaded(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
