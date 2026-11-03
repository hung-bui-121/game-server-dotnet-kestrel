using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;

namespace GameServer.Tests.Shared;

/// <summary>
/// The kit <see cref="TestHost"/> with the framework tests' defaults: Identity + Sample.Counter and the sample config
/// folder (<c>{BaseDirectory}/config</c>, copied from <c>tests/Shared/config</c>). Every other parameter is passed through.
/// </summary>
public static class SampleTestHost
{
    private const string ConfigPathKey = "GameServer:ConfigPath";

    private static string SampleConfigPath => Path.Combine(AppContext.BaseDirectory, "config");

    public static Dictionary<string, string?> DefaultSettings()
    {
        var values = TestHost.DefaultSettings();
        values[ConfigPathKey] = SampleConfigPath;
        return values;
    }

    /// <summary>Default modules: Identity + Sample.Counter.</summary>
    public static void DefaultModules(GameServerBuilder builder) => builder.AddModule<IdentityModule>().AddModule<CounterModule>();

    public static Task<TestHost> StartAsync(
        Action<GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null,
        bool testAuthentication = true,
        Action<WebApplication>? afterUseGameServer = null,
        InMemoryDocumentStore? store = null,
        FakeClock? clock = null,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        TestStoreKind? storeKind = null,
        IGameConfig? config = null) =>
        TestHost.StartAsync(
            modules ?? DefaultModules,
            WithSampleConfig(settings),
            beforeUseGameServer,
            testAuthentication,
            afterUseGameServer,
            store,
            clock,
            services,
            environmentName,
            storeKind,
            config);

    public static WebApplication BuildApp(
        Action<GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        bool testAuthentication = true,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        IGameConfig? config = null) =>
        TestHost.BuildApp(modules ?? DefaultModules, WithSampleConfig(settings), testAuthentication, services, environmentName, config);

    /// <summary>The sample config path first, so a test's own settings may still change or remove it.</summary>
    private static Action<Dictionary<string, string?>> WithSampleConfig(Action<Dictionary<string, string?>>? settings) =>
        values =>
        {
            values[ConfigPathKey] = SampleConfigPath;
            settings?.Invoke(values);
        };
}
