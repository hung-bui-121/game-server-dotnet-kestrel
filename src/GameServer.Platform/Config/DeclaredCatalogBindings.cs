using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Config;

/// <summary>
/// Which catalog types the platform registered from declarations (research R7). The collection is captured in
/// <c>AddGameServer</c> and read after <c>Build()</c> (V4): a type is <see cref="CatalogBinding.Declared"/> when its last
/// non-keyed descriptor is the platform's, else <see cref="CatalogBinding.Provided"/> (a host or test registration made
/// before or after the module).
/// </summary>
internal sealed class DeclaredCatalogBindings(IServiceCollection services)
{
    private readonly Dictionary<Type, ServiceDescriptor> _platform = [];

    /// <summary>Adds the platform's singleton for <paramref name="type"/> unless one is registered (TryAdd) and records it.</summary>
    public void Register(Type type, Func<IServiceProvider, object> factory)
    {
        if (_platform.ContainsKey(type))
        {
            // A second module declaring the same type is reported by the phase (FR-023); the first registration stays.
            return;
        }

        var descriptor = ServiceDescriptor.Singleton(type, factory);
        _platform[type] = descriptor;
        if (!services.Any(d => d.ServiceType == type && !d.IsKeyedService))
        {
            services.Add(descriptor);
        }
    }

    public CatalogBinding For(Type type, IServiceProvider provider)
    {
        var last = services.LastOrDefault(d => d.ServiceType == type && !d.IsKeyedService);
        return last is not null && _platform.TryGetValue(type, out var own) && ReferenceEquals(last, own)
            ? CatalogBinding.Declared
            : CatalogBinding.Provided(() => provider.GetRequiredService(type));
    }
}
