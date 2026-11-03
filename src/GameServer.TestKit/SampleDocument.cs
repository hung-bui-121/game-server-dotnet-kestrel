using System.Text.Json.Nodes;
using GameServer.Platform.Web.Aggregates;

namespace GameServer.TestKit;

/// <summary>One step from a state document's root to a value: a property, an element (by index), a dictionary entry's value or its key.</summary>
internal sealed record JsonStep(JsonStepKind Kind, string Name = "", int Index = 0);

internal enum JsonStepKind
{
    Property,
    Element,
    EntryValue,
    EntryKey,
}

/// <summary>A leaf of the sample document the check perturbs: its member path, CLR type and location.</summary>
internal sealed record LeafSlot(string Path, Type Type, IReadOnlyList<JsonStep> Steps);

/// <summary>What <see cref="SampleDocument.Walk"/> found: perturbable leaves, members without a usable value, supplied members.</summary>
internal sealed record SampleWalk(IReadOnlyList<LeafSlot> Leaves, IReadOnlyList<(string Path, string Reason)> Unfillable, IReadOnlyList<string> Supplied);

/// <summary>
/// The JSON document of a sample state (data-model §7.2): built from the member tree (two elements per collection, two entries per
/// dictionary, a distinct ordinal per leaf), walked to find every leaf, and copied with one leaf perturbed. The walk takes the last
/// element or entry, so a mapper that keeps only the first (<c>.First()</c>, <c>Take(1)</c>) leaves the wire unchanged and is caught.
/// </summary>
internal static class SampleDocument
{
    /// <summary>Elements per collection and entries per dictionary in a built sample; the walk perturbs the last one.</summary>
    private const int SampleCount = 2;

    public static JsonNode? Build(StateMembers members)
    {
        var ordinal = 0;
        return Build(members.Root, ref ordinal);
    }

    /// <summary>
    /// Finds the leaves of <paramref name="root"/> along the member tree and writes the supplied member values into it (those members
    /// are not walked further). Missing, null or empty values become unfillable members (never skipped silently).
    /// </summary>
    public static SampleWalk Walk(StateMembers members, JsonNode? root, IReadOnlyDictionary<string, object?> supplied)
    {
        var walk = new Walker(supplied);
        walk.Visit(members.Root, root, string.Empty, []);
        return new SampleWalk(walk.Leaves, walk.Unfillable, walk.Supplied);
    }

    /// <summary>A copy of <paramref name="root"/> with <paramref name="leaf"/> moved by one perturbation.</summary>
    public static JsonNode Perturbed(JsonNode root, LeafSlot leaf)
    {
        var copy = root.DeepClone();
        var parent = copy;
        foreach (var step in leaf.Steps.Take(leaf.Steps.Count - 1))
        {
            parent = Child(parent, step);
        }

        var last = leaf.Steps[^1];
        switch (last.Kind)
        {
            case JsonStepKind.Property:
            case JsonStepKind.EntryValue:
                parent.AsObject()[last.Name] = PerturbValue(parent.AsObject()[last.Name], leaf.Type);
                break;
            case JsonStepKind.Element:
                parent.AsArray()[last.Index] = PerturbValue(parent.AsArray()[last.Index], leaf.Type);
                break;
            default:
                var entries = parent.AsObject();
                var value = entries[last.Name];
                entries.Remove(last.Name);
                var key = SampleStates.KeyToJson(SampleStates.Perturb(leaf.Type, SampleStates.KeyFromJson(last.Name, leaf.Type)), leaf.Type);
                entries[key] = value;
                break;
        }

        return copy;
    }

    private static JsonNode? PerturbValue(JsonNode? value, Type type) =>
        SampleStates.ToJson(SampleStates.Perturb(type, SampleStates.FromJson(value, type)!), type);

    private static JsonNode Child(JsonNode node, JsonStep step) => step.Kind switch
    {
        JsonStepKind.Element => node.AsArray()[step.Index]!,
        _ => node.AsObject()[step.Name]!,
    };

    private static JsonNode? Build(StateNode node, ref int ordinal)
    {
        switch (node)
        {
            case StateObject value:
                var members = new JsonObject();
                foreach (var member in value.Members)
                {
                    members[member.JsonName] = Build(member.Node, ref ordinal);
                }

                return members;
            case StateNullable nullable:
                return Build(nullable.Inner, ref ordinal);
            case StateCollection collection:
                var elements = new JsonArray();
                for (var i = 0; i < SampleCount; i++)
                {
                    elements.Add(Build(collection.Element, ref ordinal));
                }

                return elements;
            case StateDictionary dictionary:
                // Enum keys have one sample value, so their two entries collapse into one: the walk then takes that one.
                var entries = new JsonObject();
                for (var i = 0; i < SampleCount; i++)
                {
                    if (SampleStates.KeySample(dictionary.KeyType, ++ordinal) is { } key)
                    {
                        entries[SampleStates.KeyToJson(key, dictionary.KeyType)] = Build(dictionary.Value, ref ordinal);
                    }
                }

                return entries;
            case StateLeaf leaf:
                var sample = SampleStates.Sample(leaf.Type, ++ordinal);
                return sample is null ? null : SampleStates.ToJson(sample, leaf.Type);
            default:
                return null;
        }
    }

    private sealed class Walker(IReadOnlyDictionary<string, object?> supplied)
    {
        public List<LeafSlot> Leaves { get; } = [];

        public List<(string Path, string Reason)> Unfillable { get; } = [];

        public List<string> Supplied { get; } = [];

        public void Visit(StateNode node, JsonNode? json, string path, IReadOnlyList<JsonStep> steps)
        {
            switch (node)
            {
                case StateObject value when json is JsonObject members:
                    foreach (var member in value.Members)
                    {
                        if (supplied.TryGetValue(member.Path, out var given))
                        {
                            members[member.JsonName] = SampleStates.ToJson(given, member.Node.Type);
                            Supplied.Add(member.Path);
                            continue;
                        }

                        Visit(member.Node, members[member.JsonName], member.Path, [.. steps, new JsonStep(JsonStepKind.Property, member.JsonName)]);
                    }

                    break;
                case StateNullable nullable:
                    Visit(nullable.Inner, json, path, steps);
                    break;
                case StateCollection collection when json is JsonArray { Count: > 0 } elements:
                    var index = elements.Count - 1;
                    Visit(collection.Element, elements[index], path, [.. steps, new JsonStep(JsonStepKind.Element, Index: index)]);
                    break;
                case StateDictionary dictionary when !SampleStates.IsSupportedKey(dictionary.KeyType):
                    Unfillable.Add((path, $"dictionary key type {dictionary.KeyType.Name} has no sample value"));
                    break;
                case StateDictionary dictionary when json is JsonObject { Count: > 0 } entries:
                    var (key, entry) = entries.Last();
                    Leaves.Add(new LeafSlot(path, dictionary.KeyType, [.. steps, new JsonStep(JsonStepKind.EntryKey, key)]));
                    Visit(dictionary.Value, entry, path, [.. steps, new JsonStep(JsonStepKind.EntryValue, key)]);
                    break;
                case StateLeaf leaf when !SampleStates.IsSupported(leaf.Type):
                    Unfillable.Add((path, $"type {leaf.Type.Name} has no sample value"));
                    break;
                case StateLeaf leaf when json is not null:
                    Leaves.Add(new LeafSlot(path, leaf.Type, steps));
                    break;
                case StateUnfillable unfillable:
                    Unfillable.Add((path, unfillable.Reason));
                    break;
                case StateCollection or StateDictionary:
                    Unfillable.Add((path, "empty in the sample state"));
                    break;
                default:
                    Unfillable.Add((path, "no value in the sample state"));
                    break;
            }
        }
    }
}
