using System.Collections.Concurrent;
using GameServer.Actors;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-015: the request outcome and the request event carry the name of the matched catalog route (data-model §6).</summary>
public sealed class RouteNameOutcomeTests
{
    private const string PlainRoute = "/api/v1/consumer/plain";

    /// <summary>Declared by the host, mapped with a plain string: the name comes from the catalog lookup.</summary>
    private static readonly RouteDeclaration Plain = RouteDeclaration.Post("consumer.plain", PlainRoute, SecurityProfile.Player);

    private static async Task<(RequestLogHost Session, ConcurrentDictionary<string, string?> Names)> StartAsync()
    {
        var names = new ConcurrentDictionary<string, string?>();
        var session = await RequestLogHost.StartAsync(
            beforeUseGameServer: app => app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                await next(context);
                names[CorrelationMiddleware.GetCorrelationId(context)] = context.RequestOutcome().RouteName;
            }),
            routes: [Plain],
            afterUseGameServer: app => app.MapPost(PlainRoute, () => Results.Ok()).AsPlayerEndpoint());
        return (session, names);
    }

    [Fact]
    public async Task A_command_mapped_from_its_declaration_carries_its_name()
    {
        var (session, names) = await StartAsync();
        await using var _ = session;
        var requestId = ProtobufHttp.NewRequestId();

        (await session.Host.PlayerClient("p_named").PostProtobufAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 1 }, requestId))
            .EnsureSuccessStatusCode();
        var entry = await session.EventAsync(requestId);

        entry.Properties["RouteName"].ShouldBe(IncrementHandler.Operation);
        (await NameAsync(names, requestId)).ShouldBe(IncrementHandler.Operation);
    }

    [Fact]
    public async Task A_plain_mapped_declared_route_is_named_from_the_catalog()
    {
        var (session, names) = await StartAsync();
        await using var _ = session;

        var entry = await session.EventAsync(await session.Host.PlayerClient("p_plain").PostAsync(PlainRoute, null));

        entry.Properties["RouteName"].ShouldBe(Plain.Name);
        (await NameAsync(names, (string)entry.Properties["RequestId"]!)).ShouldBe(Plain.Name);
    }

    [Fact]
    public async Task An_unmatched_path_has_no_route_name()
    {
        var (session, names) = await StartAsync();
        await using var _ = session;

        var entry = await session.EventAsync(await session.Host.PlayerClient("p_none").GetAsync("/api/v1/nope"));

        entry.Properties["RouteName"].ShouldBeNull();
        (await NameAsync(names, (string)entry.Properties["RequestId"]!)).ShouldBeNull();
    }

    /// <summary>The consumer middleware records after the response was sent: wait for it (bounded).</summary>
    private static async Task<string?> NameAsync(ConcurrentDictionary<string, string?> names, string requestId)
    {
        for (var i = 0; i < 500; i++)
        {
            if (names.TryGetValue(requestId, out var name))
            {
                return name;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"The consumer middleware saw no request {requestId}.");
    }

    [Fact]
    public void A_consumer_implementation_of_the_feature_needs_no_new_member()
    {
        IRequestOutcomeFeature consumer = new ConsumerOutcome();

        consumer.RouteName.ShouldBeNull();
    }

    /// <summary>An implementation written against slice 013: compiles unchanged thanks to the default member (N11).</summary>
    private sealed class ConsumerOutcome : IRequestOutcomeFeature
    {
        public string? ResultCode { get; set; }

        public string? Operation { get; set; }

        public bool Replayed { get; set; }

        public bool NotModified { get; set; }

        public int? StatusCode => null;

        public bool Aborted => false;

        public bool Completed => false;
    }
}
