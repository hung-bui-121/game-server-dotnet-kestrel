using GameServer.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServer.Platform.Testing;

public static class InMemoryServiceCollectionExtensions
{
    /// <summary>Registers <see cref="InMemoryDocumentStore"/> as the document store (Development/tests only).</summary>
    public static IServiceCollection AddInMemoryDocumentStore(this IServiceCollection services, InMemoryDocumentStore? store = null)
    {
        services.RemoveAll<ITransactionalDocumentStore>();
        services.RemoveAll<InMemoryDocumentStore>();
        var instance = store ?? new InMemoryDocumentStore();
        services.AddSingleton(instance);
        services.AddSingleton<ITransactionalDocumentStore>(instance);
        return services;
    }
}
