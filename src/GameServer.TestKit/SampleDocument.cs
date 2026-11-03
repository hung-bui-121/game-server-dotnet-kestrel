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
/// dictionary, a distinct ordinal per leaf), walked to find every leaf, and copied with one leaf perturbed. The walk takes the first
/// and the last element or entry, so a mapper that keeps only one end (<c>.First()</c>, <c>Take(1)</c>, <c>.Last()</c>,
/// <c>Skip(1)</c>) leaves the wire unchanged for the other and is caught.
/// </summary>
internal static class SampleDocument
{
    /// <summary>Elements per collection and entries per dictionary in a built sample; the walk perturbs the first and the last.</summary>
    private const int SampleCount = 2;

    /// <summary>Enum samples outside any collection or dictionary take the first non-zero value.</summary>
    private const int RootPosition = 1;

    public static JsonNode? Build(StateMembers members)
    {
        var ordinal = 0;
        return Build(members.Root, ref ordinal, RootPosition);
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

    /// <summary>
    /// A copy of <paramref name="root"/> with <paramref name="leaf"/> moved by one perturbation that avoids the values the same member
    /// holds in the other elements or entries (a dictionary key avoids the other keys and keeps its place in the entry order).
    /// </summary>
    public static JsonNode Perturbed(JsonNode root, LeafSlot leaf)
    {
        var copy = root.DeepClone();
        var parent = copy;
        foreach (var step in leaf.Steps.Take(leaf.Steps.Count - 1))
        {
            parent = Child(parent, step);
        }

        var last = leaf.Steps[^1];
        if (last.Kind == JsonStepKind.EntryKey)
        {
            var entries = parent.AsObject();
            var index = entries.IndexOf(last.Name);
            var value = entries[last.Name];
            entries.RemoveAt(index);
            var others = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            var key = SampleStates.Perturb(
                leaf.Type, SampleStates.KeyFromJson(last.Name, leaf.Type), candidate => others.Contains(SampleStates.KeyToJson(candidate, leaf.Type)), others.Count);
            entries.Insert(index, SampleStates.KeyToJson(key, leaf.Type), value);
            return copy;
        }

        var siblings = Siblings(copy, leaf.Steps);
        var current = last.Kind == JsonStepKind.Element ? parent.AsArray()[last.Index] : parent.AsObject()[last.Name];
        var perturbed = SampleStates.ToJson(
            SampleStates.Perturb(
                leaf.Type,
                SampleStates.FromJson(current, leaf.Type)!,
                candidate => siblings.Exists(sibling => JsonNode.DeepEquals(sibling, SampleStates.ToJson(candidate, leaf.Type))),
                siblings.Count),
            leaf.Type);
        if (last.Kind == JsonStepKind.Element)
        {
            parent.AsArray()[last.Index] = perturbed;
        }
        else
        {
            parent.AsObject()[last.Name] = perturbed;
        }

        return copy;
    }

    private static JsonNode Child(JsonNode node, JsonStep step) => step.Kind switch
    {
        JsonStepKind.Element => node.AsArray()[step.Index]!,
        _ => node.AsObject()[step.Name]!,
    };

    /// <summary>
    /// The values at the leaf's place in the other elements (or entries) of its innermost collection (or dictionary); a sibling that
    /// lacks the place (another nested key, a shorter list) has none.
    /// </summary>
    private static List<JsonNode?> Siblings(JsonNode root, IReadOnlyList<JsonStep> steps)
    {
        var siblings = new List<JsonNode?>();
        var container = steps.Count - 1;
        while (container >= 0 && steps[container].Kind is not (JsonStepKind.Element or JsonStepKind.EntryValue))
        {
            container--;
        }

        if (container < 0)
        {
            return siblings;
        }

        var node = root;
        for (var i = 0; i < container; i++)
        {
            node = Child(node, steps[i]);
        }

        var own = steps[container];
        var others = own.Kind == JsonStepKind.Element
            ? node.AsArray().Where((_, index) => index != own.Index)
            : node.AsObject().Where(entry => entry.Key != own.Name).Select(entry => entry.Value);
        foreach (var other in others)
        {
            if (TryFollow(other, steps, container + 1, out var value))
            {
                siblings.Add(value);
            }
        }

        return siblings;
    }

    private static bool TryFollow(JsonNode? node, IReadOnlyList<JsonStep> steps, int from, out JsonNode? value)
    {
        value = node;
        for (var i = from; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (value)
            {
                case JsonArray elements when step.Kind == JsonStepKind.Element && step.Index < elements.Count:
                    value = elements[step.Index];
                    break;
                case JsonObject members when step.Kind != JsonStepKind.Element && members.TryGetPropertyValue(step.Name, out var member):
                    value = member;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="position"/> is the 1-based index of the innermost collection element or dictionary entry being built; enum
    /// samples use it (see <see cref="SampleStates.Sample"/>), every other leaf the running ordinal.
    /// </summary>
    private static JsonNode? Build(StateNode node, ref int ordinal, int position)
    {
        switch (node)
        {
            case StateObject value:
                var members = new JsonObject();
                foreach (var member in value.Members)
                {
                    members[member.JsonName] = Build(member.Node, ref ordinal, position);
                }

                return members;
            case StateNullable nullable:
                return Build(nullable.Inner, ref ordinal, position);
            case StateCollection collection:
                var elements = new JsonArray();
                for (var i = 1; i <= SampleCount; i++)
                {
                    elements.Add(Build(collection.Element, ref ordinal, i));
                }

                return elements;
            case StateDictionary dictionary:
                // An enum key with a single non-zero value has one sample key, so its two entries collapse into one.
                var entries = new JsonObject();
                for (var i = 1; i <= SampleCount; i++)
                {
                    if (SampleStates.KeySample(dictionary.KeyType, ++ordinal, i) is { } key)
                    {
                        entries[SampleStates.KeyToJson(key, dictionary.KeyType)] = Build(dictionary.Value, ref ordinal, i);
                    }
                }

                return entries;
            case StateLeaf leaf:
                var sample = SampleStates.Sample(leaf.Type, ++ordinal, position);
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
                    foreach (var index in Ends(elements.Count))
                    {
                        Visit(collection.Element, elements[index], path, [.. steps, new JsonStep(JsonStepKind.Element, Index: index)]);
                    }

                    break;
                case StateDictionary dictionary when !SampleStates.IsSupportedKey(dictionary.KeyType):
                    Unfillable.Add((path, $"dictionary key type {dictionary.KeyType.Name} has no sample value"));
                    break;
                case StateDictionary dictionary when json is JsonObject { Count: > 0 } entries:
                    foreach (var index in Ends(entries.Count))
                    {
                        var (key, entry) = entries.GetAt(index);
                        Leaves.Add(new LeafSlot(path, dictionary.KeyType, [.. steps, new JsonStep(JsonStepKind.EntryKey, key)]));
                        Visit(dictionary.Value, entry, path, [.. steps, new JsonStep(JsonStepKind.EntryValue, key)]);
                    }

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

        /// <summary>The first and the last index (one index for a single element or entry).</summary>
        private static int[] Ends(int count) => count == 1 ? [0] : [0, count - 1];
    }
}
