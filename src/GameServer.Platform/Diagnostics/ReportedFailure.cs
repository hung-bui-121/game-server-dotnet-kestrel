namespace GameServer.Platform.Diagnostics;

/// <summary>
/// Marks an exception whose failure was already logged once where it happened (a typed read failure, a failed account creation),
/// so the error middleware maps it to its envelope without a second line (T082 m1; research R5 "Middleware"). The request log
/// still records the code.
/// </summary>
internal static class ReportedFailure
{
    /// <summary><see cref="Exception.Data"/> key of the marker.</summary>
    private const string MarkerKey = "GameServer.ReportedFailure";

    /// <summary>Marks <paramref name="exception"/> as reported and returns it (for <c>throw ReportedFailure.Mark(…)</c>).</summary>
    public static TException Mark<TException>(TException exception)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[MarkerKey] = true;
        return exception;
    }

    /// <summary>True for an exception marked by <see cref="Mark{TException}"/>.</summary>
    public static bool IsReported(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[MarkerKey] is true;
    }
}
