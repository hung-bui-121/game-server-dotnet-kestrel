extern alias sdk;

using sdk::GameServer.Client;
using Shouldly;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T055 (FR-040, VERIFY V1, Planning note 12): the five call shapes of V1 compile here without CS0121 and each sends exactly
/// one request; only the shapes with a string (or null) fourth argument take the caller's id. The C# 9 / Unity compiler check is
/// T003 (stub) and T068 (real package).
/// </summary>
public sealed class OverloadBindingTests
{
    private const string CallerId = "7b11d2a9-0c0d-4e6f-8a8b-9c0d3f2a9c1e";

    public static TheoryData<string> Shapes() => new("(r, q, p)", "(r, q, p, default)", "(r, q, p, ct)", "(r, q, p, \"id\")", "(r, q, p, null)");

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Each_call_shape_sends_one_request(string shape)
    {
        var (client, transport, _, _) = PendingClients.Create(store: null);
        var request = PendingClients.Request();
        var parser = SdkCore.SnapshotResponse.Parser;
        using var cts = new CancellationTokenSource();

        var result = shape switch
        {
            "(r, q, p)" => await client.SendCommandAsync(PendingClients.Route, request, parser),
            "(r, q, p, default)" => await client.SendCommandAsync(PendingClients.Route, request, parser, default),
            "(r, q, p, ct)" => await client.SendCommandAsync(PendingClients.Route, request, parser, cts.Token),
            "(r, q, p, \"id\")" => await client.SendCommandAsync(PendingClients.Route, request, parser, CallerId),
            _ => await client.SendCommandAsync(PendingClients.Route, request, parser, null),
        };

        var sent = PendingClients.RequestIdOf(transport.Requests.ShouldHaveSingleItem());
        result.RequestId.ShouldBe(sent);
        if (shape == "(r, q, p, \"id\")")
        {
            sent.ShouldBe(CallerId);
        }
        else
        {
            sent.ShouldNotBe(CallerId);
        }
    }
}
