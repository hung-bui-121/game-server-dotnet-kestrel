namespace GameServer.TestKit;

/// <summary>A claim marker the handler reserved (<c>TryClaimAsync</c> returned true): collection, key and stored attributes.</summary>
public sealed record RecordedClaim(string Collection, string Key, IReadOnlyDictionary<string, string> Attributes);
