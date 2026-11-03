using GameServer.Commands;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// Explicit game overrides applied after framework modules (EXT-02): replace a policy/service, a command handler or a
/// wire mapper. Overrides stay inside the common pipeline (EXT-04).
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

    /// <summary>
    /// Replaces the wire mapper of <paramref name="aggregateId"/> (BC-4: two mappers for one aggregate fail startup, so this is the
    /// documented way to change a framework section's payload). Removes the mappers registered for the id so far through
    /// <c>AddAggregateWireMapper</c> and adds the typed one; without an earlier mapper it simply registers it. The override must keep
    /// the message of the typed mapper it replaces (<typeparamref name="TWire"/>): the client parses the section with that message's
    /// parser, so another message fails startup (FG-021); a new payload needs a new aggregate.
    /// </summary>
    public ExtensionRegistry ReplaceWireMapper<TState, TWire>(string aggregateId, Func<TState, TWire> map, Action<WireMapperOptions>? configure = null)
        where TState : class
        where TWire : class, IMessage<TWire>, new()
    {
        ArgumentException.ThrowIfNullOrEmpty(aggregateId);
        ArgumentNullException.ThrowIfNull(map);

        // AddAggregateWireMapper registers instances; a mapper registered by type or factory stays and is reported as a duplicate.
        var earlier = services
            .Where(d => d.ServiceType == typeof(IAggregateWireMapper) && !d.IsKeyedService && d.ImplementationInstance is IAggregateWireMapper mapper &&
                        string.Equals(mapper.AggregateId, aggregateId, StringComparison.Ordinal))
            .ToArray();
        foreach (var descriptor in earlier)
        {
            services.Remove(descriptor);
        }

        // The first earlier mapper is the one that served (first registration wins); a second override keeps the original message.
        var replaced = earlier.Select(d => d.ImplementationInstance).FirstOrDefault();
        var options = new WireMapperOptions();
        configure?.Invoke(options);
        services.AddSingleton<IAggregateWireMapper>(new AggregateWireMapper<TState, TWire>(aggregateId, map, options)
        {
            ReplacedMessage = (replaced as IWireMapperReplacement)?.ReplacedMessage ?? (replaced as IAggregateWireContract)?.Message.FullName,
        });
        return this;
    }
}
