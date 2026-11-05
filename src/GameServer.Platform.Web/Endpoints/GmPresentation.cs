using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>How a console confirms a GM mutation (<c>x-gm-confirm</c>, Q7).</summary>
public enum GmConfirm
{
    /// <summary>One confirmation click (every mutation by default).</summary>
    Simple,

    /// <summary>The operator types the target before the call is sent (dangerous operations).</summary>
    Typed,
}

/// <summary>Confirmation level of a GM mutation, read by the GM document (<c>x-gm-confirm</c>).</summary>
public sealed record GmConfirmMetadata(GmConfirm Confirm);

/// <summary>Marks a paged GM list (<c>x-gm-paged</c>, GM-06) and names the item type of its <see cref="GmPage{T}"/>.</summary>
public sealed record GmPagedMetadata(Type ItemType);

/// <summary>A query parameter a GM handler reads by hand, documented in the GM document (the handler keeps its own check).</summary>
public sealed record GmQueryParameterMetadata(string Name, bool Required, int? MinLength = null, int? MaxLength = null, string? Description = null);

/// <summary>Marker: the endpoint requires the <c>X-Request-Id</c> UUID header (documented as a required header).</summary>
public sealed class GmRequestIdMetadata
{
    private GmRequestIdMetadata()
    {
    }

    public static GmRequestIdMetadata Instance { get; } = new();
}

/// <summary>
/// The property's values come from the GM GET route named <see cref="RouteName"/> (<c>x-gm-options</c>): the route answers a
/// <see cref="GmOptionList"/>. Presentation only: the server still validates the submitted value. A name that is not a GM GET
/// route of the host fails startup (FR-045).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class GmOptionsAttribute : Attribute
{
    public GmOptionsAttribute(string routeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeName);
        RouteName = routeName;
    }

    public string RouteName { get; }
}

/// <summary>One choice of an option route: the value sent back to the server and the label shown to the operator.</summary>
public sealed record GmOption(string Value, string Label);

/// <summary>Answer of a GM option route (<c>[GmOptions]</c>).</summary>
public sealed record GmOptionList(IReadOnlyList<GmOption> Items);

/// <summary>
/// Presentation API of GM endpoints (slice 017): metadata read by the GM document only; no request handling changes.
/// </summary>
public static class GmPresentationExtensions
{
    internal const string PageSizeParameter = "pageSize";
    internal const string CursorParameter = "cursor";

    /// <summary>Sets the confirmation level of a GM mutation (<c>x-gm-confirm</c>; GET operations never carry it).</summary>
    public static TBuilder WithGmConfirm<TBuilder>(this TBuilder builder, GmConfirm confirm)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new GmConfirmMetadata(confirm));
        return builder;
    }

    /// <summary>
    /// <c>Produces&lt;GmPage&lt;T&gt;&gt;</c>; documents the required <c>pageSize</c> (1..200) and the optional <c>cursor</c>
    /// query parameters read by <see cref="GmHttp.ReadPageRequest"/>, and marks the list <c>x-gm-paged</c>.
    /// </summary>
    public static RouteHandlerBuilder ProducesGmPage<T>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Produces<GmPage<T>>()
            .WithMetadata(
                new GmPagedMetadata(typeof(T)),
                new GmQueryParameterMetadata(PageSizeParameter, Required: true),
                new GmQueryParameterMetadata(CursorParameter, Required: false));
    }

    /// <summary>Documents a query parameter the handler reads by hand (the handler keeps its own check).</summary>
    public static TBuilder WithGmQueryParameter<TBuilder>(this TBuilder builder, string name, bool required, int? maxLength = null, int? minLength = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.WithMetadata(new GmQueryParameterMetadata(name, required, minLength, maxLength));
        return builder;
    }

    /// <summary>Documents the required <c>X-Request-Id</c> header of a GM mutation mapped outside <c>MapGmCommand</c>.</summary>
    public static TBuilder WithGmRequestId<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(GmRequestIdMetadata.Instance);
        return builder;
    }
}
