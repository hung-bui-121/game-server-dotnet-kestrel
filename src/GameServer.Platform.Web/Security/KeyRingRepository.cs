using System.Text.Json.Nodes;
using System.Xml.Linq;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace GameServer.Platform.Web.Security;

/// <summary>
/// Shared key ring of the deployment in one document <c>dataprotection/keyring</c> (OPS-04, research R6).
/// <see cref="IXmlRepository"/> is synchronous while the store is async-only (PERF-01 forbids sync-over-async), so
/// Data Protection reads an in-memory snapshot and new keys are queued; <see cref="KeyRingService"/> loads and
/// flushes asynchronously. Keys are created ahead of their activation, so the queue delay never matters.
/// </summary>
public sealed class KeyRingRepository(IKeyValueStore store) : IXmlRepository, IDisposable
{
    public static readonly DocumentKey DocumentKey = new(PlatformCollections.DataProtection, "keyring");

    private const int WriteAttempts = 5;

    private readonly object _gate = new();
    private readonly List<XElement> _pending = [];
    private XElement[] _snapshot = [];
    private CancellationTokenSource _changed = new();

    /// <summary>Cancelled whenever the snapshot gains keys, so Data Protection drops its cached key ring.</summary>
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
        Publish(Read(document));
    }

    /// <summary>Writes queued keys, merging with keys written concurrently by other instances (CAS, retried).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            XElement[] pending;
            lock (_gate)
            {
                pending = [.. _pending];
            }

            if (pending.Length == 0)
            {
                return;
            }

            var existing = await store.GetAsync(DocumentKey, cancellationToken);
            var merged = Merge(Read(existing), pending);
            var content = new JsonObject
            {
                ["keys"] = new JsonArray(merged.Select(k => (JsonNode)k.ToString(SaveOptions.DisableFormatting)).ToArray()),
                ["rev"] = (existing?.Content["rev"]?.GetValue<long>() ?? 0) + 1,
            };
            var written = existing is null
                ? await store.InsertAsync(DocumentKey, content, null, cancellationToken)
                : await store.ReplaceAsync(existing, content, null, cancellationToken);
            if (written)
            {
                lock (_gate)
                {
                    _pending.RemoveAll(p => pending.Any(w => KeyId(w) == KeyId(p)));
                }

                Publish(merged);
                return;
            }

            if (attempt >= WriteAttempts)
            {
                throw new InvalidOperationException("The Data Protection key ring changed concurrently too many times; new key not written.");
            }
        }
    }

    private void Publish(IReadOnlyList<XElement> stored)
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            var next = Merge(stored, _pending);
            if (next.Count != _snapshot.Length || next.Any(k => _snapshot.All(s => KeyId(s) != KeyId(k))))
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
