using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServer.Platform.Testing;

public static class InMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryDocumentStore"/> as the transactional and key-value store (Development/tests only).
    /// Without an explicit instance, the store uses the registered <see cref="IClock"/> for expiry.
    /// </summary>
    public static IServiceCollection AddInMemoryDocumentStore(this IServiceCollection services, InMemoryDocumentStore? store = null)
    {
        services.RemoveAll<ITransactionalDocumentStore>();
        services.RemoveAll<IKeyValueStore>();
        services.RemoveAll<InMemoryDocumentStore>();
        if (store is null)
        {
            services.AddSingleton(sp => new InMemoryDocumentStore(sp.GetRequiredService<IClock>()));
        }
        else
        {
            services.AddSingleton(store);
        }

        services.AddSingleton<ITransactionalDocumentStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.AddSingleton<IKeyValueStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        return services;
    }
}
