using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// Hand-over from <c>UseGameServer</c> to <see cref="EndpointSetValidationService"/> (research R8): the application whose final
/// endpoint set is validated before serving. Written once, after <c>UseGameServer</c>'s own validation succeeded.
/// </summary>
internal sealed class EndpointValidationState
{
    private IEndpointRouteBuilder? _routes;

    /// <summary>Null when <c>UseGameServer</c> was never called (or failed): the pre-serving run then does nothing.</summary>
    public IEndpointRouteBuilder? Routes => _routes;

    public void Validated(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (Interlocked.CompareExchange(ref _routes, routes, null) is not null)
        {
            throw new InvalidOperationException("UseGameServer was called twice on one host.");
        }
    }
}
