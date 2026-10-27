using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace GameServer.Platform.Web.Security;

/// <summary>Audit entry written in the same transaction as new keys (key + content built from the change details).</summary>
public sealed record KeyRingAudit(DocumentKey Key, Func<object, JsonObject> Build);

public enum KeyRingFlushOutcome
{
    /// <summary>Nothing was pending.</summary>
    Nothing,

    Written,

    /// <summary>The audit entry already existed: the same request committed before; local keys were discarded.</summary>
    Replayed,

    /// <summary>A first key was not needed: another instance already wrote an active key; ours was discarded.</summary>
    Superseded,
}

/// <summary>
/// Shared key ring of the deployment in one document <c>dataprotection/keyring</c> (OPS-04, research R6).
/// <see cref="IXmlRepository"/> is synchronous while the store is async-only (PERF-01 forbids sync-over-async), so
/// Data Protection reads an in-memory snapshot and new keys are queued; <see cref="KeyRingService"/> loads and
/// flushes asynchronously. Every write is an audited transaction (SEC-14): the key ring changes together with its
/// audit entry, and the audit key makes a retried rotation a replay (review M3).
/// </summary>
public sealed class KeyRingRepository(IKeyValueStore store, IAuditedStore audited, GameServerOptions gameServer, IClock clock) : IXmlRepository, IDisposable
{
    public static readonly DocumentKey DocumentKey = new(PlatformCollections.DataProtection, "keyring");

    private readonly object _gate = new();
    private readonly List<XElement> _pending = [];
    private XElement[] _snapshot = [];
    private CancellationTokenSource _changed = new();

    /// <summary>Cancelled whenever Data Protection must rebuild its key ring (new keys loaded, a key activating).</summary>
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_gate)
            {
                return _changed.Token;
            }
        }
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            return [.. _snapshot];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_gate)
        {
            _pending.Add(new XElement(element));
            _snapshot = [.. _snapshot, new XElement(element)];
        }
    }

    public bool HasPendingWrites
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>Reloads the shared document; local keys not yet written stay in the snapshot.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(DocumentKey, cancellationToken);
        Publish(Read(document), force: false);
    }

    /// <summary>Makes Data Protection rebuild its cached key ring (e.g. when a key activates, review M4).</summary>
    public void Invalidate()
    {
        XElement[] stored;
        lock (_gate)
        {
            stored = _snapshot.Where(s => _pending.All(p => KeyId(p) != KeyId(s))).ToArray();
        }

        Publish(stored, force: true);
    }

    /// <summary>
    /// Writes queued keys with their audit entry in one transaction, merging keys written concurrently by other
    /// instances. <paramref name="onlyIfNoActiveKey"/>: a first key is discarded when the ring already has an active
    /// key (instances starting together, review m1). Without <paramref name="audit"/> a system entry is written.
    /// </summary>
    public async Task<KeyRingFlushOutcome> FlushAsync(KeyRingAudit? audit, bool onlyIfNoActiveKey, CancellationToken cancellationToken)
    {
        XElement[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
        }

        if (pending.Length == 0)
        {
            return KeyRingFlushOutcome.Nothing;
        }

        var keyIds = pending.Select(KeyId).ToArray();
        var entry = audit ?? SystemAudit(keyIds);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await FlushOnceAsync(pending, keyIds, entry, onlyIfNoActiveKey);
            }
            catch (DocumentExistsException ex) when (ex.Key == DocumentKey && attempt < 3)
            {
                // Another instance created the ring first (Couchbase reports the lost insert race this way): read it
                // again and merge.
            }
        }
    }

    private async Task<KeyRingFlushOutcome> FlushOnceAsync(XElement[] pending, string[] keyIds, KeyRingAudit entry, bool onlyIfNoActiveKey)
    {
        var (outcome, stored) = await audited.RunAsync(async tx =>
        {
            var read = await tx.GetManyAsync([DocumentKey, entry.Key]);
            var current = Read(read[0]);
            if (read[1] is not null)
            {
                return (KeyRingFlushOutcome.Replayed, (IReadOnlyList<XElement>)current);
            }

            if (onlyIfNoActiveKey && HasActiveKey(current, clock.UtcNow))
            {
                return (KeyRingFlushOutcome.Superseded, current);
            }

            var merged = Merge(current, pending);
            var content = new JsonObject
            {
                ["keys"] = new JsonArray(merged.Select(k => (JsonNode)k.ToString(SaveOptions.DisableFormatting)).ToArray()),
                ["rev"] = (read[0]?.Content["rev"]?.GetValue<long>() ?? 0) + 1,
            };
            if (read[0] is { } existing)
            {
                await tx.ReplaceAsync(existing, content);
            }
            else
            {
                await tx.InsertAsync(DocumentKey, content);
            }

            await tx.InsertAsync(entry.Key, entry.Build(new { keyIds }));
            return (KeyRingFlushOutcome.Written, merged);
        });

        lock (_gate)
        {
            // Written: now stored. Replayed/Superseded: never stored and never will be, so discard.
            _pending.RemoveAll(p => keyIds.Contains(KeyId(p)));
        }

        Publish(stored, force: outcome != KeyRingFlushOutcome.Written);
        return outcome;
    }

    /// <summary>True when the ring has a non-revoked key that is active at <paramref name="now"/> (Data Protection XML).</summary>
    public static bool HasActiveKey(IEnumerable<XElement> elements, DateTimeOffset now)
    {
        var list = elements.ToArray();
        var revoked = list.Where(e => e.Name.LocalName == "revocation")
            .Select(e => (string?)e.Element("key")?.Attribute("id") ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(e => e.Name.LocalName == "key" && !revoked.Contains((string?)e.Attribute("id") ?? string.Empty))
            .Any(e => Date(e, "activationDate") <= now && Date(e, "expirationDate") > now);
    }

    private static DateTimeOffset Date(XElement key, string name) =>
        DateTimeOffset.TryParse((string?)key.Element(name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : DateTimeOffset.MinValue;

    private KeyRingAudit SystemAudit(IReadOnlyList<string> keyIds)
    {
        var entryId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("dataprotection|" + string.Join(',', keyIds))));
        return new KeyRingAudit(
            new DocumentKey(AuditedStore.AuditCollection, "gm_audit::" + entryId),
            details => new JsonObject
            {
                ["entryId"] = entryId,
                ["kind"] = "platform_audit",
                ["gameId"] = gameServer.GameId,
                ["environmentId"] = gameServer.EnvironmentId,
                ["actorId"] = "system:dataprotection",
                ["operation"] = "dataprotection.key_created",
                ["targetStaffId"] = "system",
                ["committedAtUtc"] = AggregateDocumentSerializer.FormatTime(clock.UtcNow),
                ["payload"] = new JsonObject
                {
                    ["actorId"] = "system:dataprotection",
                    ["permission"] = "system",
                    ["action"] = "dataprotection.key_created",
                    ["targetStaffId"] = "system",
                    ["reason"] = "Scheduled Data Protection key creation (OPS-04)",
                    ["after"] = System.Text.Json.JsonSerializer.SerializeToNode(details, GameServerJson.Options),
                    ["correlationId"] = entryId,
                },
            });
    }

    private void Publish(IReadOnlyList<XElement> stored, bool force)
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            var next = Merge(stored, _pending);
            if (force || next.Count != _snapshot.Length || next.Any(k => _snapshot.All(s => KeyId(s) != KeyId(k))))
            {
                _snapshot = [.. next];
                previous = _changed;
                _changed = new CancellationTokenSource();
            }
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private static XElement[] Read(StoredDocument? document) =>
        document?.Content["keys"]?.AsArray().Select(k => XElement.Parse(k!.GetValue<string>())).ToArray() ?? [];

    private static List<XElement> Merge(IEnumerable<XElement> stored, IEnumerable<XElement> local)
    {
        var result = stored.ToList();
        result.AddRange(local.Where(l => result.All(s => KeyId(s) != KeyId(l))).Select(l => new XElement(l)));
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _changed.Dispose();
        }
    }

    /// <summary>Key elements are <c>&lt;key id="…"&gt;</c>; revocations have their own id-less element names.</summary>
    private static string KeyId(XElement element) => (string?)element.Attribute("id") ?? element.ToString(SaveOptions.DisableFormatting);
}
