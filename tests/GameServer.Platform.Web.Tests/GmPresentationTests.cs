using GameServer.Actors;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T008 (FR-048, FR-040, FR-041): each presentation extension adds exactly its metadata to a mapped endpoint and
/// nothing else changes.
/// </summary>
public sealed class GmPresentationTests
{
    private sealed record ItemView(string Id);

    [Fact]
    public void With_gm_confirm_adds_the_confirm_metadata()
    {
        var endpoint = Map(app => app.MapPost("/gm/v1/x", () => Results.Ok()).WithGmConfirm(GmConfirm.Typed));

        endpoint.Metadata.GetOrderedMetadata<GmConfirmMetadata>().ShouldBe([new GmConfirmMetadata(GmConfirm.Typed)]);
        endpoint.Metadata.GetMetadata<GmPagedMetadata>().ShouldBeNull();
        endpoint.Metadata.GetMetadata<GmRequestIdMetadata>().ShouldBeNull();
    }

    [Fact]
    public void Produces_gm_page_adds_the_page_type_the_paged_marker_and_the_two_query_parameters()
    {
        var endpoint = Map(app => app.MapGet("/gm/v1/x", () => Results.Ok()).ProducesGmPage<ItemView>());

        endpoint.Metadata.GetOrderedMetadata<GmPagedMetadata>().ShouldBe([new GmPagedMetadata(typeof(ItemView))]);
        endpoint.Metadata.GetOrderedMetadata<GmQueryParameterMetadata>().ShouldBe(
        [
            new GmQueryParameterMetadata("pageSize", Required: true),
            new GmQueryParameterMetadata("cursor", Required: false),
        ]);
        var produces = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().ShouldHaveSingleItem();
        produces.Type.ShouldBe(typeof(GmPage<ItemView>));
        produces.StatusCode.ShouldBe(StatusCodes.Status200OK);
        endpoint.Metadata.GetMetadata<GmConfirmMetadata>().ShouldBeNull();
    }

    [Fact]
    public void With_gm_query_parameter_adds_one_parameter()
    {
        var endpoint = Map(app => app.MapGet("/gm/v1/x", () => Results.Ok()).WithGmQueryParameter("displayName", required: true, maxLength: 64, minLength: 1));

        endpoint.Metadata.GetOrderedMetadata<GmQueryParameterMetadata>().ShouldBe([new GmQueryParameterMetadata("displayName", true, 1, 64)]);
        endpoint.Metadata.GetMetadata<GmPagedMetadata>().ShouldBeNull();
    }

    [Fact]
    public void With_gm_request_id_adds_the_marker_once()
    {
        var endpoint = Map(app => app.MapPut("/gm/v1/x", () => Results.Ok()).WithGmRequestId());

        endpoint.Metadata.GetOrderedMetadata<GmRequestIdMetadata>().ShouldBe([GmRequestIdMetadata.Instance]);
    }

    [Fact]
    public void Gm_options_names_its_route()
    {
        new GmOptionsAttribute("economy.resource_options").RouteName.ShouldBe("economy.resource_options");
        Should.Throw<ArgumentException>(() => new GmOptionsAttribute(" "));
    }

    public sealed record NoteBody(string? Text, string? Reason) : IGmCommandBody;

    public sealed record NoteCommand(string Text);

    private static readonly RouteDeclaration NoteRoute = new(
        "probe.note", ["POST"], "/gm/v1/players/{playerId}/note", SecurityProfile.Gm, RouteSurface.GmApi, "probe.note", "probe.edit");

    /// <summary>Slice 017 T013 (FR-003, FR-006): every MapGmCommand overload documents its body and the request-id header.</summary>
    [Fact]
    public void A_gm_command_carries_its_body_type_and_the_request_id_marker_through_every_overload()
    {
        RouteEndpoint[] endpoints =
        [
            Map(app => app.MapGmCommand<NoteBody, NoteCommand, string>(NoteRoute.Template, "probe.note", "probe.edit", b => new NoteCommand(b.Text!))),
            Map(app => app.MapGmCommand<NoteBody, NoteCommand, string>(NoteRoute, b => new NoteCommand(b.Text!))),
            Map(app => app.MapGmCommand<NoteBody, NoteCommand, string>(NoteRoute, b => new NoteCommand(b.Text!), (_, _) => Task.CompletedTask)),
        ];

        foreach (var endpoint in endpoints)
        {
            var accepts = endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>().ShouldHaveSingleItem();
            accepts.RequestType.ShouldBe(typeof(NoteBody));
            accepts.ContentTypes.ShouldBe(["application/json"]);
            accepts.IsOptional.ShouldBeFalse();
            endpoint.Metadata.GetOrderedMetadata<GmRequestIdMetadata>().ShouldBe([GmRequestIdMetadata.Instance]);
        }
    }

    internal static RouteEndpoint Map(Action<WebApplication> map)
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        map(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ShouldHaveSingleItem();
    }
}
