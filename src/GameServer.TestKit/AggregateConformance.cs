using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Web.Aggregates;
using Google.Protobuf;

namespace GameServer.TestKit;

/// <summary>
/// The conformance algorithm for one typed mapper (research R16, data-model §7): sample JSON → state → map → parse; every proto field
/// still default is reported (minus may-stay-default), then each leaf is perturbed once: an unchanged message means the member is not
/// on the wire (unless hidden), a changed one for a hidden member means the declaration is stale or the member leaks.
/// </summary>
internal sealed class AggregateConformance
{
    private readonly string _id;
    private readonly IAggregateWireContract _contract;
    private readonly AggregateWireMapperRegistry _mappers;
    private readonly List<WireConformanceFinding> _findings;
    private readonly HashSet<(WireConformanceProblem, string)> _reported = [];

    private AggregateConformance(string id, IAggregateWireContract contract, AggregateWireMapperRegistry mappers, List<WireConformanceFinding> findings)
    {
        _id = id;
        _contract = contract;
        _mappers = mappers;
        _findings = findings;
    }

    public static void Run(
        string id,
        IAggregateWireContract contract,
        AggregateWireMapperRegistry mappers,
        WireConformanceOptions options,
        List<WireConformanceFinding> findings,
        List<string> notes) =>
        new AggregateConformance(id, contract, mappers, findings).Run(options, notes);

    private void Run(WireConformanceOptions options, List<string> notes)
    {
        var members = StateMembers.Of(_contract.StateType);
        var supplied = options.MemberValuesOf(_id);
        foreach (var path in supplied.Keys.Where(path => !members.Resolve(path, out _)))
        {
            throw new ArgumentException(
                $"[aggregate {_id}] WithMemberValue path '{path}' does not exist on {_contract.StateType.Name}. Fix: use C# member names joined by '.'.",
                nameof(options));
        }

        var root = SampleRoot(options.SampleStateOf(_id));
        var walk = SampleDocument.Walk(members, root, supplied);
        foreach (var (path, reason) in walk.Unfillable)
        {
            Report(WireConformanceProblem.MemberNotFillable, path,
                $"the kit cannot build a sample value ({reason}). Fix: WithMemberValue(\"{_id}\", \"{path}\", value) or WithSampleState(\"{_id}\", state) (FG-027).");
        }

        notes.AddRange(walk.Supplied.Select(path => $"[aggregate {_id}] note: {path} uses the supplied value and is not perturbed."));
        if (!TryMap(root, out var baseline, out var error))
        {
            Report(WireConformanceProblem.SampleNotSerializable, string.Empty,
                $"the sample state cannot be mapped: {error}. Fix: WithSampleState(\"{_id}\", state) with a valid state, or WithMemberValue for the member (FG-027).");
            return;
        }

        foreach (var field in ProtoDefaults.Of(baseline).Where(field => !Covers(_contract.MayStayDefaultFields, field)))
        {
            Report(WireConformanceProblem.FieldAtDefault, field,
                $"the mapper leaves this field at its proto default although every state member is set. Fix: set it in the mapper, or declare " +
                $"o.MayStayDefault(\"{field}\") when its default is meaningful (FG-027).");
        }

        foreach (var leaf in walk.Leaves)
        {
            if (!TryMap(SampleDocument.Perturbed(root!, leaf), out var perturbed, out error))
            {
                Report(WireConformanceProblem.SampleNotSerializable, leaf.Path,
                    $"the state with this member changed cannot be mapped: {error}. Fix: WithSampleState or WithMemberValue (FG-027).");
                continue;
            }

            var onWire = !perturbed.Equals(baseline);
            var hidden = Covers(_contract.HiddenMembers, leaf.Path);
            if (!onWire && !hidden)
            {
                Report(WireConformanceProblem.MemberNotOnWire, leaf.Path,
                    $"changing this member does not change the wire message. Fix: map it, or declare o.Hidden(\"{leaf.Path}\") when it stays on the server (FG-027).");
            }
            else if (onWire && hidden)
            {
                Report(WireConformanceProblem.HiddenMemberOnWire, leaf.Path,
                    "the member is declared hidden but changing it changes the wire message. Fix: remove it from o.Hidden(…) or stop mapping it (FG-027).");
            }
        }
    }

    private JsonNode? SampleRoot(object? state)
    {
        if (state is null)
        {
            return SampleDocument.Build(StateMembers.Of(_contract.StateType));
        }

        return _contract.StateType.IsInstanceOfType(state)
            ? SampleStates.ToJson(state, _contract.StateType)
            : throw new ArgumentException(
                $"[aggregate {_id}] WithSampleState got a {state.GetType().Name}; the aggregate's state type is {_contract.StateType.Name}.", nameof(state));
    }

    /// <summary>Deserializes the document as the store would, maps it with the registered mapper and parses the payload.</summary>
    private bool TryMap(JsonNode? json, out IMessage message, out string error)
    {
        message = null!;
        object? state;
        try
        {
            state = json.Deserialize(_contract.StateType, GameServerJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (state is null)
        {
            error = "the document deserializes to null";
            return false;
        }

        try
        {
            message = _contract.Parser.ParseFrom(_mappers.ToPayload(_id, state));
        }
#pragma warning disable CA1031 // A throwing game mapper is a finding of the check, not a crash of the consumer's test run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = $"the mapper threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>True when <paramref name="path"/> is one of <paramref name="declared"/> or below one of them.</summary>
    private static bool Covers(IReadOnlyList<string> declared, string path) =>
        declared.Any(entry => path == entry || path.StartsWith(entry + ".", StringComparison.Ordinal));

    private void Report(WireConformanceProblem problem, string path, string message)
    {
        if (_reported.Add((problem, path)))
        {
            _findings.Add(new WireConformanceFinding(_id, problem, path, message));
        }
    }
}
