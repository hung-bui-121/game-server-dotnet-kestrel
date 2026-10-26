using GameServer.Actors;

namespace GameServer.Platform.Web.Routing;

/// <summary>Security profile of an endpoint. Exactly one per endpoint (API-02, SEC-08).</summary>
public sealed record SecurityProfileMetadata(SecurityProfile Profile);

/// <summary>GM permission required by an endpoint (SEC-07).</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>Marks endpoints whose responses and errors are Protobuf (API-01, API-04).</summary>
public sealed class ProtobufEndpointMetadata
{
    public static ProtobufEndpointMetadata Instance { get; } = new();
}
