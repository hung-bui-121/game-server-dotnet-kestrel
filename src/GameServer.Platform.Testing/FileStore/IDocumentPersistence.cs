using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Testing;

/// <summary>
/// Receives every committed change of an <see cref="InMemoryDocumentStore"/> under the store lock, before it is visible
/// (research R6). Throws to refuse the write (nothing is applied); returning normally means the change is durable.
/// </summary>
internal interface IDocumentPersistence
{
    void Persist(IReadOnlyList<DocumentChange> changes);
}

/// <summary>One document of a commit. <see cref="Content"/> null = removal; <see cref="ExpiresAt"/> is absolute UTC (DAT-03).</summary>
/// <remarks>The content is the instance the store keeps: persistence must only read it.</remarks>
internal readonly record struct DocumentChange(DocumentKey Key, JsonObject? Content, DateTimeOffset? ExpiresAt);
