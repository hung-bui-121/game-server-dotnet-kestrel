using GameServer.Actors;

namespace GameServer.Platform.Web.Routing;

/// <summary>Security profile of an endpoint. Exactly one per endpoint (API-02, SEC-08).</summary>
public sealed record SecurityProfileMetadata(SecurityProfile Profile);

/// <summary>GM permission required by an endpoint (SEC-07).</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>Endpoints subject to the maintenance/version gate (GATE-01, GATE-02): all Player endpoints and login.</summary>
public sealed class GateMetadata
{
    public static GateMetadata Instance { get; } = new();
}

/// <summary>Marks endpoints whose responses and errors are Protobuf (API-01, API-04).</summary>
public sealed class ProtobufEndpointMetadata
{
    public static ProtobufEndpointMetadata Instance { get; } = new();
}

/// <summary>
/// Operation run by a command endpoint (MapPlayerCommand, MapDevCommand, MapGmCommand). Startup checks it against the commands
/// the modules registered, so a route for an unknown operation fails the start instead of answering NOT_FOUND (ARC-07).
/// </summary>
public sealed record CommandOperationMetadata(string Operation);
