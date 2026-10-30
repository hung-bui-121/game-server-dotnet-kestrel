using GameServer.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GameServer.Platform.Couchbase;

public static class CouchbaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers Couchbase as the document store: one shared connection (PERF-03), the store, the provisioner (startup
    /// provisioning or schema check before the web server accepts requests; <see cref="IStoreProvisioner"/> for
    /// <c>--provision</c>) and <see cref="CouchbaseSettingsCheck"/>, which reports missing or
    /// out-of-range settings in the one startup report (FR-013).
    /// </summary>
    public static IServiceCollection AddCouchbaseDocumentStore(this IServiceCollection services, CouchbaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<CouchbaseConnection>();
        services.RemoveAll<ITransactionalDocumentStore>();
        services.RemoveAll<IKeyValueStore>();
        services.AddSingleton<CouchbaseDocumentStore>();
        services.AddSingleton<ITransactionalDocumentStore>(sp => sp.GetRequiredService<CouchbaseDocumentStore>());
        services.AddSingleton<IKeyValueStore>(sp => sp.GetRequiredService<CouchbaseDocumentStore>());
        services.RemoveAll<IPagedQueryStore>();
        services.AddSingleton<IPagedQueryStore>(sp => sp.GetRequiredService<CouchbaseDocumentStore>());
        services.RemoveAll<IMaintenanceQueryStore>();
        services.AddSingleton<IMaintenanceQueryStore>(sp => sp.GetRequiredService<CouchbaseDocumentStore>());
        services.AddSingleton<CouchbaseProvisioner>();
        services.AddHostedService(sp => sp.GetRequiredService<CouchbaseProvisioner>());
        services.AddSingleton<IStoreInitializer>(sp => sp.GetRequiredService<CouchbaseProvisioner>());
        services.AddSingleton<IStoreProvisioner>(sp => sp.GetRequiredService<CouchbaseProvisioner>());
        services.AddSingleton<IStartupCheck>(sp => new CouchbaseSettingsCheck(
            options,
            sp.GetRequiredService<GameServerOptions>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
        return services;
    }
}
