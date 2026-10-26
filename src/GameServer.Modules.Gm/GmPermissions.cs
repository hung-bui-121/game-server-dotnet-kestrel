namespace GameServer.Modules.Gm;

/// <summary>Permissions owned by the GM module (SEC-07 catalog). Other modules declare their own (e.g. <c>resource.adjust</c>).</summary>
public static class GmPermissions
{
    /// <summary>Required by every GM endpoint that is not more specific (session, profile of the staff).</summary>
    public const string Access = "gm.access";

    public const string PlayerRead = "player.read";

    /// <summary>Shows identity mapping keys (PII, GM-02).</summary>
    public const string PlayerReadPii = "player.read.pii";

    public static IReadOnlyList<string> All { get; } = [Access, PlayerRead, PlayerReadPii];
}
