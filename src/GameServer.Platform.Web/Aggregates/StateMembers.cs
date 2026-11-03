using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace GameServer.Platform.Web.Aggregates;

/// <summary>A node of a state's member tree (data-model §7.1).</summary>
internal abstract record StateNode(Type Type);

/// <summary>A value the serializer writes as one JSON token (numbers, strings, enums, dates, ids, bytes, untyped values).</summary>
internal sealed record StateLeaf(Type Type) : StateNode(Type);

/// <summary>An object with members; each member is a node of its own.</summary>
internal sealed record StateObject(Type Type, IReadOnlyList<StateMember> Members) : StateNode(Type);

/// <summary><c>Nullable&lt;T&gt;</c> around its inner node.</summary>
internal sealed record StateNullable(Type Type, StateNode Inner) : StateNode(Type);

/// <summary>A collection: the element node is transparent in member paths.</summary>
internal sealed record StateCollection(Type Type, StateNode Element) : StateNode(Type);

/// <summary>A dictionary: the key belongs to the dictionary's own path, the value node is transparent.</summary>
internal sealed record StateDictionary(Type Type, Type KeyType, StateNode Value) : StateNode(Type);

/// <summary>An object the tree does not expand (a type cycle or deeper than <see cref="StateMembers.MaxDepth"/>).</summary>
internal sealed record StateUnfillable(Type Type, string Reason) : StateNode(Type);

/// <summary>A persisted member: C# name, JSON name, path (C# names joined by '.', elements transparent) and its node.</summary>
internal sealed record StateMember(string Name, string JsonName, string Path, StateNode Node);

/// <summary>
/// The members of an aggregate state as the document serializer persists them (research R16): a <c>JsonPropertyInfo</c> of
/// <see cref="GameServerJson.Options"/> with a setter or a constructor parameter; computed get-only properties are not state.
/// Shared by the startup check of hidden members and the test kit's conformance check, so both agree on member paths.
/// </summary>
internal sealed class StateMembers
{
    /// <summary>Object nesting below the state that is still expanded (data-model §7.1).</summary>
    public const int MaxDepth = 8;

    private static readonly ConcurrentDictionary<Type, StateMembers> Cache = new();

    private StateMembers(Type stateType, StateNode root, IReadOnlyList<StateMember> all)
    {
        StateType = stateType;
        Root = root;
        All = all;
    }

    public Type StateType { get; }

    public StateNode Root { get; }

    /// <summary>Every member of the tree, depth first in declaration order.</summary>
    public IReadOnlyList<StateMember> All { get; }

    public static StateMembers Of(Type stateType)
    {
        ArgumentNullException.ThrowIfNull(stateType);
        return Cache.GetOrAdd(stateType, Build);
    }

    /// <summary>The member at <paramref name="path"/> (C# member names joined by '.'; collection and dictionary elements transparent).</summary>
    public bool Resolve(string path, [NotNullWhen(true)] out StateMember? member)
    {
        member = null;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var node = Root;
        StateMember? found = null;
        foreach (var segment in path.Split('.'))
        {
            found = Members(node)?.FirstOrDefault(m => string.Equals(m.Name, segment, StringComparison.Ordinal));
            if (found is null)
            {
                return false;
            }

            node = found.Node;
        }

        member = found;
        return member is not null;
    }

    /// <summary>The members reachable from <paramref name="node"/> through nullable, collection and dictionary wrappers; null for leaves.</summary>
    private static IReadOnlyList<StateMember>? Members(StateNode node) => node switch
    {
        StateObject value => value.Members,
        StateNullable nullable => Members(nullable.Inner),
        StateCollection collection => Members(collection.Element),
        StateDictionary dictionary => Members(dictionary.Value),
        _ => null,
    };

    private static StateMembers Build(Type stateType)
    {
        var all = new List<StateMember>();
        var root = Node(stateType, string.Empty, 0, [], all);
        return new StateMembers(stateType, root, all);
    }

    private static StateNode Node(Type type, string path, int depth, HashSet<Type> enclosing, List<StateMember> all)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return new StateNullable(type, Node(inner, path, depth, enclosing, all));
        }

        var info = GameServerJson.Options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return new StateCollection(type, Node(info.ElementType!, path, depth, enclosing, all));
            case JsonTypeInfoKind.Dictionary:
                return new StateDictionary(type, info.KeyType!, Node(info.ElementType!, path, depth, enclosing, all));
            case JsonTypeInfoKind.Object:
                return ObjectNode(type, info, path, depth, enclosing, all);
            default:
                return new StateLeaf(type);
        }
    }

    private static StateNode ObjectNode(Type type, JsonTypeInfo info, string path, int depth, HashSet<Type> enclosing, List<StateMember> all)
    {
        if (enclosing.Contains(type))
        {
            return new StateUnfillable(type, "type cycle");
        }

        if (depth > MaxDepth)
        {
            return new StateUnfillable(type, $"nested deeper than {MaxDepth} objects");
        }

        enclosing.Add(type);
        var members = new List<StateMember>();
        foreach (var property in info.Properties.Where(p => p.Set is not null || p.AssociatedParameter is not null))
        {
            var name = (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;
            var memberPath = path.Length == 0 ? name : path + "." + name;
            var position = all.Count;
            var member = new StateMember(name, property.Name, memberPath, Node(property.PropertyType, memberPath, depth + 1, enclosing, all));

            // Before the members of its own node, so the list reads depth first.
            all.Insert(position, member);
            members.Add(member);
        }

        enclosing.Remove(type);
        return new StateObject(type, members);
    }
}
