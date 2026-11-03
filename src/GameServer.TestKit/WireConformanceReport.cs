using System.Text;

namespace GameServer.TestKit;

/// <summary>What a wire-mapper conformance check found wrong (FG-027, data-model §7.4).</summary>
public enum WireConformanceProblem
{
    /// <summary>A proto field is still at its default after mapping a sample state with every member set.</summary>
    FieldAtDefault,

    /// <summary>Changing a persisted, non-hidden state member does not change the wire message.</summary>
    MemberNotOnWire,

    /// <summary>Changing a member declared hidden changes the wire message (the declaration is stale or the member leaks).</summary>
    HiddenMemberOnWire,

    /// <summary>The kit cannot build a sample value for a member (unsupported type, type cycle, empty in the supplied state).</summary>
    MemberNotFillable,

    /// <summary>The sample state does not round-trip through the document serializer, or the mapper throws for it.</summary>
    SampleNotSerializable,

    /// <summary>The aggregate's mapper was registered without its message type.</summary>
    UntypedMapper,

    /// <summary>A client-visible aggregate has no wire mapper.</summary>
    NoMapper,
}

/// <summary>One finding: the aggregate, the problem, the member or proto field path (empty for the whole aggregate) and the fix.</summary>
public sealed record WireConformanceFinding(string AggregateId, WireConformanceProblem Problem, string Path, string Message)
{
    /// <summary><c>[aggregate {id}] {problem}: {path} — {fix}</c>.</summary>
    public override string ToString() =>
        Path.Length == 0 ? $"[aggregate {AggregateId}] {Problem} — {Message}" : $"[aggregate {AggregateId}] {Problem}: {Path} — {Message}";
}

/// <summary>Result of <see cref="WireConformance"/>: the aggregates checked and every finding; passed when there is none.</summary>
public sealed class WireConformanceReport
{
    internal WireConformanceReport(IReadOnlyList<string> checkedAggregates, IReadOnlyList<WireConformanceFinding> findings, IReadOnlyList<string> notes)
    {
        CheckedAggregates = checkedAggregates;
        Findings = findings;
        Notes = notes;
    }

    public IReadOnlyList<string> CheckedAggregates { get; }

    public IReadOnlyList<WireConformanceFinding> Findings { get; }

    public bool Passed => Findings.Count == 0;

    /// <summary>Members whose sample was supplied (<see cref="WireConformanceOptions.WithMemberValue"/>) and therefore not perturbed.</summary>
    internal IReadOnlyList<string> Notes { get; }

    /// <summary>Throws <see cref="WireConformanceException"/> with one line per finding when not passed.</summary>
    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new WireConformanceException(this);
        }
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Passed ? "Wire conformance passed" : $"Wire conformance failed with {Findings.Count} finding(s)")
            .Append(" for ").Append(CheckedAggregates.Count == 0 ? "no aggregate" : string.Join(", ", CheckedAggregates)).Append('.');
        foreach (var finding in Findings)
        {
            text.Append('\n').Append(finding);
        }

        foreach (var note in Notes)
        {
            text.Append('\n').Append(note);
        }

        return text.ToString();
    }
}

/// <summary>Thrown by <see cref="WireConformanceReport.ThrowIfFailed"/>; the message lists every finding.</summary>
public sealed class WireConformanceException : Exception
{
    public WireConformanceException(WireConformanceReport report)
        : base((report ?? throw new ArgumentNullException(nameof(report))).ToString())
    {
        Report = report;
    }

    public WireConformanceReport Report { get; }
}
