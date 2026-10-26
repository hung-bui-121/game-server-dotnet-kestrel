using GameServer.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServer.Platform.Couchbase;

public static class CouchbaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers Couchbase as the document store: one shared connection (PERF-03), the store and the
    /// startup provisioner (runs before the web server accepts requests).
    /// </summary>
    public static IServiceCollection AddCouchbaseDocumentStore(this IServiceCollection services, CouchbaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            throw new GameServerStartupException("[couchbase] Couchbase:BucketName is not set. Fix: configure the bucket of this environment (DAT-01).");
        }

        services.AddSingleton(options);
        services.AddSingleton<CouchbaseConnection>();
        services.RemoveAll<ITransactionalDocumentStore>();
        services.AddSingleton<ITransactionalDocumentStore, CouchbaseDocumentStore>();
        services.AddSingleton<CouchbaseProvisioner>();
        services.AddHostedService(sp => sp.GetRequiredService<CouchbaseProvisioner>());
        return services;
    }
}
