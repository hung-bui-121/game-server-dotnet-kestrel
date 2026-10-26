namespace GameServer.Errors;

/// <summary>
/// Pipeline error carrying a catalog code. The web layer turns it into an ErrorEnvelope or Problem Details;
/// <see cref="Detail"/> is for logs only and is never sent to clients.
/// </summary>
public sealed class GameServerException : Exception
{
    public GameServerException(ErrorCode code, string? detail = null, Exception? inner = null)
        : base(detail ?? code.SafeMessage, inner)
    {
        Code = code;
        Detail = detail;
    }

    public ErrorCode Code { get; }

    public string? Detail { get; }

    /// <summary>Optional retry hint in milliseconds when <see cref="ErrorCode.Retryable"/>.</summary>
    public int? RetryAfterMs { get; init; }
}
