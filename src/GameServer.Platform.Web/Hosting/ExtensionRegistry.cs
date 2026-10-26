using GameServer.Commands;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// Explicit game overrides applied after framework modules (EXT-02): replace a policy/service or a
/// command handler. Overrides stay inside the common pipeline (EXT-04).
/// </summary>
public sealed class ExtensionRegistry(IServiceCollection services, ModuleRegistry registry)
{
    /// <summary>Replaces the registered implementation of <typeparamref name="TService"/>, keeping its lifetime.</summary>
    public ExtensionRegistry ReplacePolicy<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(TService))
            ?? throw new GameServerStartupException(
                $"[extensions] ReplacePolicy<{typeof(TService).Name}>: no default implementation is registered. " +
                "Fix: register the module that provides it before ConfigureExtensions.");
        services.RemoveAll<TService>();
        services.Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), existing.Lifetime));
        return this;
    }

    /// <summary>Replaces the handler of a registered command (PLY-06, EXT-02).</summary>
    public ExtensionRegistry ReplaceHandler<TRequest, TResult, THandler>(string operation)
        where THandler : ICommandHandler<TRequest, TResult>
    {
        registry.ReplaceHandler(operation, typeof(THandler));
        return this;
    }
}
