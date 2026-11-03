using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Aggregates;

/// <summary>Maps the domain state of a client-visible aggregate to its Protobuf wire message (API-10).</summary>
public interface IAggregateWireMapper
{
    string AggregateId { get; }

    ByteString ToPayload(object state);
}

/// <summary>Static wire contract of a typed mapper (FG-028): what the catalog and the conformance check read.</summary>
public interface IAggregateWireContract
{
    string AggregateId { get; }

    Type StateType { get; }

    MessageDescriptor Message { get; }

    MessageParser Parser { get; }

    IReadOnlyList<string> HiddenMembers { get; }

    IReadOnlyList<string> MayStayDefaultFields { get; }
}

public sealed class AggregateWireMapper<TState>(string aggregateId, Func<TState, IMessage> map) : IAggregateWireMapper
{
    public string AggregateId { get; } = aggregateId;

    public ByteString ToPayload(object state) => map((TState)state).ToByteString();
}

/// <summary>A wire mapper that knows its state and message types (FG-028), with the members it keeps off the wire (FR-060).</summary>
public sealed class AggregateWireMapper<TState, TWire> : IAggregateWireMapper, IAggregateWireContract
    where TState : class
    where TWire : class, IMessage<TWire>, new()
{
    private readonly Func<TState, TWire> _map;

    public AggregateWireMapper(string aggregateId, Func<TState, TWire> map, WireMapperOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(aggregateId);
        ArgumentNullException.ThrowIfNull(map);
        AggregateId = aggregateId;
        _map = map;
        HiddenMembers = options?.HiddenMembers ?? [];
        MayStayDefaultFields = options?.MayStayDefaultFields ?? [];
        Message = new TWire().Descriptor;
        Parser = new MessageParser<TWire>(() => new TWire());
    }

    public string AggregateId { get; }

    public Type StateType => typeof(TState);

    public MessageDescriptor Message { get; }

    public MessageParser Parser { get; }

    public IReadOnlyList<string> HiddenMembers { get; }

    public IReadOnlyList<string> MayStayDefaultFields { get; }

    public TWire Map(TState state) => _map(state);

    public ByteString ToPayload(object state) => _map((TState)state).ToByteString();
}

/// <summary>Declarations next to the mapper registration (FR-060).</summary>
public sealed class WireMapperOptions
{
    private readonly SortedSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _mayStayDefault = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> HiddenMembers => [.. _hidden];

    internal IReadOnlyList<string> MayStayDefaultFields => [.. _mayStayDefault];

    /// <summary>State members kept on the server on purpose: C# member names joined by '.', collection elements transparent.</summary>
    public WireMapperOptions Hidden(params string[] memberPaths)
    {
        ArgumentNullException.ThrowIfNull(memberPaths);
        _hidden.UnionWith(memberPaths);
        return this;
    }

    /// <summary>Proto fields whose meaningful value may be the default (enum zero): proto field names joined by '.'.</summary>
    public WireMapperOptions MayStayDefault(params string[] protoFieldPaths)
    {
        ArgumentNullException.ThrowIfNull(protoFieldPaths);
        _mayStayDefault.UnionWith(protoFieldPaths);
        return this;
    }
}

/// <summary>One aggregate id registered with more than one wire mapper (reported at startup, BC-4).</summary>
internal sealed record DuplicateWireMapper(string AggregateId, int Count);

public sealed class AggregateWireMapperRegistry
{
    private readonly Dictionary<string, IAggregateWireMapper> _mappers = new(StringComparer.Ordinal);

    public AggregateWireMapperRegistry(IEnumerable<IAggregateWireMapper> mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapper in mappers)
        {
            _mappers[mapper.AggregateId] = mapper;
            counts[mapper.AggregateId] = counts.GetValueOrDefault(mapper.AggregateId) + 1;
        }

        Duplicates = counts
            .Where(pair => pair.Value > 1)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DuplicateWireMapper(pair.Key, pair.Value))
            .ToArray();
    }

    /// <summary>Aggregate ids registered more than once, ordinal by id.</summary>
    internal IReadOnlyList<DuplicateWireMapper> Duplicates { get; }

    public bool Has(string aggregateId) => _mappers.ContainsKey(aggregateId);

    public ByteString ToPayload(string aggregateId, object state) =>
        _mappers.TryGetValue(aggregateId, out var mapper)
            ? mapper.ToPayload(state)
            : throw new InvalidOperationException($"No wire mapper registered for aggregate '{aggregateId}'.");

    /// <summary>The typed contract of the aggregate's mapper; false when it has no mapper or an untyped one.</summary>
    public bool TryGetContract(string aggregateId, [NotNullWhen(true)] out IAggregateWireContract? contract)
    {
        contract = _mappers.GetValueOrDefault(aggregateId) as IAggregateWireContract;
        return contract is not null;
    }
}

public static class AggregateWireMapperServiceCollectionExtensions
{
    /// <summary>Registers the wire mapping of a client-visible aggregate with its message type (FG-028).</summary>
    public static IServiceCollection AddAggregateWireMapper<TState, TWire>(
        this IServiceCollection services,
        string aggregateId,
        Func<TState, TWire> map,
        Action<WireMapperOptions>? configure = null)
        where TState : class
        where TWire : class, IMessage<TWire>, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new WireMapperOptions();
        configure?.Invoke(options);
        return services.AddSingleton<IAggregateWireMapper>(new AggregateWireMapper<TState, TWire>(aggregateId, map, options));
    }

    /// <summary>Registers the wire mapping of a client-visible aggregate.</summary>
    public static IServiceCollection AddAggregateWireMapper<TState>(this IServiceCollection services, string aggregateId, Func<TState, IMessage> map) =>
        services.AddSingleton<IAggregateWireMapper>(new AggregateWireMapper<TState>(aggregateId, map));
}
