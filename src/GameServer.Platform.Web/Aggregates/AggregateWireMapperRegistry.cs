using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Aggregates;

/// <summary>Maps the domain state of a client-visible aggregate to its Protobuf wire message (API-10).</summary>
public interface IAggregateWireMapper
{
    string AggregateId { get; }

    ByteString ToPayload(object state);
}

public sealed class AggregateWireMapper<TState>(string aggregateId, Func<TState, IMessage> map) : IAggregateWireMapper
{
    public string AggregateId { get; } = aggregateId;

    public ByteString ToPayload(object state) => map((TState)state).ToByteString();
}

public sealed class AggregateWireMapperRegistry
{
    private readonly Dictionary<string, IAggregateWireMapper> _mappers = new(StringComparer.Ordinal);

    public AggregateWireMapperRegistry(IEnumerable<IAggregateWireMapper> mappers)
    {
        foreach (var mapper in mappers)
        {
            _mappers[mapper.AggregateId] = mapper;
        }
    }

    public bool Has(string aggregateId) => _mappers.ContainsKey(aggregateId);

    public ByteString ToPayload(string aggregateId, object state) =>
        _mappers.TryGetValue(aggregateId, out var mapper)
            ? mapper.ToPayload(state)
            : throw new InvalidOperationException($"No wire mapper registered for aggregate '{aggregateId}'.");
}

public static class AggregateWireMapperServiceCollectionExtensions
{
    /// <summary>Registers the wire mapping of a client-visible aggregate.</summary>
    public static IServiceCollection AddAggregateWireMapper<TState>(this IServiceCollection services, string aggregateId, Func<TState, IMessage> map) =>
        services.AddSingleton<IAggregateWireMapper>(new AggregateWireMapper<TState>(aggregateId, map));
}
