using GameServer.Aggregates;

namespace GameServer.Modules.Identity;

/// <summary>
/// State of the mandatory <c>profile</c> aggregate (DAT-09). Slice 001 only registers it; login, session epoch
/// handling and identities arrive with slice 002.
/// </summary>
public sealed record ProfileState
{
    public string DisplayName { get; init; } = string.Empty;

    public long SessionEpoch { get; init; }
}

public static class IdentityAggregates
{
    public const string Profile = "profile";

    public static PlayerAggregateDescriptor<ProfileState> ProfileDescriptor { get; } =
        new(Profile, schemaVersion: 1, clientVisible: true, createDefault: () => new ProfileState());
}
