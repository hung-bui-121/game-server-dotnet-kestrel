using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;
using Google.Protobuf.Collections;

namespace GameServer.Platform.Web.Aggregates;

/// <summary>
/// The one place a section payload is built (snapshot, login, command response): a wire mapper that throws makes the section
/// unavailable (reported once, research R7) instead of failing the response. A section is either written with its payload or
/// listed as unavailable, never both and never with an empty payload (an old SDK would read an empty payload as a valid default).
/// </summary>
public sealed class SectionWriter(AggregateWireMapperRegistry mappers, AggregateReadFailures failures)
{
    /// <summary>
    /// Writes <paramref name="loaded"/> into <paramref name="sections"/> in their order, and <paramref name="readFailures"/> plus the
    /// mapper failures into <paramref name="unavailable"/>, ordered by aggregate id.
    /// </summary>
    public void WriteSections(
        string playerId,
        IEnumerable<LoadedAggregate> loaded,
        IEnumerable<AggregateReadFailure> readFailures,
        RepeatedField<Section> sections,
        RepeatedField<UnavailableSection> unavailable)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(sections);
        var listed = new List<AggregateReadFailure>(readFailures ?? []);
        foreach (var aggregate in loaded)
        {
            var id = aggregate.Descriptor.AggregateId;
            if (IsListed(listed, id))
            {
                continue;
            }

            if (TryMap(playerId, id, aggregate.State, aggregate.StoredSchemaVersion, aggregate.Descriptor.SchemaVersion, listed) is { } payload)
            {
                sections.Add(new Section { Id = id, Rev = aggregate.Rev, Payload = payload });
            }
        }

        WriteUnavailable(listed, unavailable);
    }

    /// <summary>
    /// Writes the committed or replayed <paramref name="changes"/> into <paramref name="updates"/>, and <paramref name="readFailures"/>
    /// (a replay that could not read an aggregate) plus the mapper failures into <paramref name="unavailable"/>, ordered by id. A
    /// mapper failure after a commit never undoes the commit: the section is only listed.
    /// </summary>
    public void WriteUpdates(
        string playerId,
        IEnumerable<AggregateChange> changes,
        IEnumerable<AggregateReadFailure> readFailures,
        RepeatedField<SectionUpdate> updates,
        RepeatedField<UnavailableSection> unavailable)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(updates);
        var listed = new List<AggregateReadFailure>(readFailures ?? []);
        foreach (var change in changes)
        {
            if (IsListed(listed, change.AggregateId))
            {
                continue;
            }

            // The state of a change is the code's schema; the versions are not known here and stay 0 (they appear on no wire or line).
            if (TryMap(playerId, change.AggregateId, change.State, storedSchemaVersion: 0, codeSchemaVersion: 0, listed) is { } payload)
            {
                updates.Add(new SectionUpdate
                {
                    Id = change.AggregateId,
                    BaseRev = change.BaseRev,
                    NewRev = change.NewRev,
                    Payload = payload,
                    Full = true,
                });
            }
        }

        WriteUnavailable(listed, unavailable);
    }

    private ByteString? TryMap(string playerId, string aggregateId, object state, int storedSchemaVersion, int codeSchemaVersion, List<AggregateReadFailure> listed)
    {
        try
        {
            return mappers.ToPayload(aggregateId, state);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Game code: only the exception type is kept (API-04).
            var type = ex.GetType();
            var failure = new AggregateReadFailure(
                aggregateId,
                playerId,
                AggregateReadFailureReason.MapperFailed,
                type.FullName ?? type.Name,
                storedSchemaVersion,
                codeSchemaVersion);
            failures.Report(failure);
            listed.Add(failure);
            return null;
        }
    }

    private static bool IsListed(List<AggregateReadFailure> listed, string aggregateId) =>
        listed.Exists(f => string.Equals(f.AggregateId, aggregateId, StringComparison.Ordinal));

    private static void WriteUnavailable(List<AggregateReadFailure> listed, RepeatedField<UnavailableSection> unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);
        foreach (var failure in listed.OrderBy(f => f.AggregateId, StringComparer.Ordinal))
        {
            unavailable.Add(new UnavailableSection { Id = failure.AggregateId, Code = failure.Code.Name, CodeNumber = failure.Code.Number });
        }
    }
}
