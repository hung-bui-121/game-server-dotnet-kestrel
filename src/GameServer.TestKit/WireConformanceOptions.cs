namespace GameServer.TestKit;

/// <summary>Inputs of a conformance check the kit cannot build itself (data-model §7.2).</summary>
public sealed class WireConformanceOptions
{
    private readonly Dictionary<string, object> _sampleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, object?>> _memberValues = new(StringComparer.Ordinal);

    /// <summary>Use <paramref name="state"/> as the sample (perturbations still apply to its JSON).</summary>
    public WireConformanceOptions WithSampleState(string aggregateId, object state)
    {
        ArgumentException.ThrowIfNullOrEmpty(aggregateId);
        ArgumentNullException.ThrowIfNull(state);
        _sampleStates[aggregateId] = state;
        return this;
    }

    /// <summary>
    /// Sample value for a member the kit cannot fill (C# member names joined by '.', elements transparent). The member is then
    /// not perturbed: the report's text notes it.
    /// </summary>
    public WireConformanceOptions WithMemberValue(string aggregateId, string memberPath, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(aggregateId);
        ArgumentException.ThrowIfNullOrEmpty(memberPath);
        if (!_memberValues.TryGetValue(aggregateId, out var values))
        {
            _memberValues[aggregateId] = values = new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        values[memberPath] = value;
        return this;
    }

    internal object? SampleStateOf(string aggregateId) => _sampleStates.GetValueOrDefault(aggregateId);

    internal IReadOnlyDictionary<string, object?> MemberValuesOf(string aggregateId) =>
        _memberValues.TryGetValue(aggregateId, out var values) ? values : new Dictionary<string, object?>();
}
