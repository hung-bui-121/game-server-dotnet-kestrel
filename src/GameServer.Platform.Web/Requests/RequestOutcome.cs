using GameServer.Errors;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Requests;

/// <summary>
/// The framework's <see cref="IRequestOutcomeFeature"/>: mutable while the request runs, final after <see cref="Complete"/>
/// (research R11, data-model §2).
/// </summary>
internal sealed class RequestOutcome : IRequestOutcomeFeature
{
    public string? ResultCode { get; set; }

    public string? Operation { get; set; }

    public bool Replayed { get; set; }

    public bool NotModified { get; set; }

    public int? StatusCode { get; private set; }

    public bool Aborted { get; private set; }

    public bool Completed { get; private set; }

    /// <summary>
    /// Finalization after the framework pipeline: a client gone before the response started → no status, <c>ABORTED</c>;
    /// an exception escaping the pipeline → 500 (unless the response started), <c>INTERNAL</c>; otherwise the sent status and,
    /// when no writer set a code, <c>OK</c> (&lt; 400) or <c>HTTP_{status}</c>. A code a writer or consumer endpoint set is
    /// kept except in the first two cases.
    /// </summary>
    public void Complete(HttpContext context, Exception? failure)
    {
        if (context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            StatusCode = null;
            Aborted = true;
            ResultCode = RequestResultCodes.Aborted;
        }
        else if (failure is not null)
        {
            StatusCode = context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
            ResultCode = CoreErrorCodes.Internal.Name;
        }
        else
        {
            var status = context.Response.StatusCode;
            StatusCode = status;
            ResultCode ??= status < StatusCodes.Status400BadRequest ? CoreErrorCodes.Ok.Name : RequestResultCodes.ForStatus(status);
        }

        Completed = true;
    }
}
