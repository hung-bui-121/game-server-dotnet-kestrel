namespace GameServer.Errors;

/// <summary>Entry of the shared error-code catalog (API-05, API-11).</summary>
/// <param name="Number">Unique number inside the owning module's range.</param>
/// <param name="Name">UPPER_SNAKE name sent on the wire.</param>
/// <param name="HttpStatus">HTTP status class; business codes use 200.</param>
/// <param name="Retryable">Whether the client may retry with the same requestId.</param>
/// <param name="SafeMessage">Message safe to show to any client.</param>
public sealed record ErrorCode(int Number, string Name, int HttpStatus, bool Retryable, string SafeMessage)
{
    public override string ToString() => $"{Name}({Number})";
}

/// <summary>Error codes owned by one module, constrained to a numeric range.</summary>
public sealed class ErrorCodeSet
{
    public ErrorCodeSet(int rangeStart, int rangeEnd, params ErrorCode[] codes)
    {
        if (rangeEnd < rangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), "rangeEnd must be >= rangeStart.");
        }

        ArgumentNullException.ThrowIfNull(codes);
        var missing = Array.FindIndex(codes, code => code is null);
        if (missing >= 0)
        {
            // Static fields initialize in declaration order: a set built before the ErrorCode fields it lists sees null.
            throw new ArgumentException(
                $"ErrorCodeSet {rangeStart}-{rangeEnd}: entry {missing} (0-based) is null. " +
                "Fix: declare ErrorCode fields above the ErrorCodeSet that lists them (static fields initialize in declaration order).",
                nameof(codes));
        }

        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        Codes = codes;
    }

    public int RangeStart { get; }

    public int RangeEnd { get; }

    public IReadOnlyList<ErrorCode> Codes { get; }

    /// <summary>A module that declares no error codes.</summary>
    public static ErrorCodeSet None { get; } = new(-1, -1);

    public bool IsNone => RangeStart < 0;
}
