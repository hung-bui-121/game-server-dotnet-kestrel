namespace GameServer.Platform.Web.Catalog;

/// <summary>
/// A client-visible aggregate with a wire mapper (FG-028): what Unity receives as section <paramref name="Id"/>.
/// <paramref name="MessageFullName"/> is null for a mapper registered without its message type.
/// </summary>
public sealed record SectionEntry(
    string Id,
    string Owner,
    CatalogOrigin Origin,
    string? MessageFullName,
    IReadOnlyList<string> HiddenMembers,
    IReadOnlyList<string> MayStayDefaultFields);
