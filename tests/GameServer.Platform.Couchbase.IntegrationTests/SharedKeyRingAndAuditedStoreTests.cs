using System.Text.Json.Nodes;
using System.Xml.Linq;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Security;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 007 on a real Couchbase: the shared Data Protection key ring document (OPS-04, ACC-49) and audited
/// transactions for non-player documents (SEC-14).
/// </summary>
public sealed class SharedKeyRingAndAuditedStoreTests(CouchbaseFixture fixture) : IClassFixture<CouchbaseFixture>
{
    private IKeyValueStore Kv => (IKeyValueStore)fixture.Store;

    private static XElement Key(string id) => new("key", new XAttribute("id", id), new XAttribute("version", 1), new XElement("payload", id));

    [Fact]
    public async Task Two_instances_writing_keys_concurrently_both_end_up_in_the_shared_ring()
    {
        fixture.RequireCouchbase();
        var audited = new AuditedStore(fixture.Store, fixture.GameServer);
        var clock = new GameServer.Platform.Testing.FakeClock();
        using var a = new KeyRingRepository(Kv, audited, fixture.GameServer, clock);
        using var b = new KeyRingRepository(Kv, audited, fixture.GameServer, clock);
        var idA = Guid.NewGuid().ToString("D");
        var idB = Guid.NewGuid().ToString("D");
        a.StoreElement(Key(idA), "a");
        b.StoreElement(Key(idB), "b");

        // Concurrent flushes conflict on the one document; CAS retries merge instead of overwriting.
        var outcomes = await Task.WhenAll(
            a.FlushAsync(audit: null, onlyIfNoActiveKey: false, CancellationToken.None),
            b.FlushAsync(audit: null, onlyIfNoActiveKey: false, CancellationToken.None));
        outcomes.ShouldAllBe(o => o == KeyRingFlushOutcome.Written);
        await a.LoadAsync(CancellationToken.None);
        await b.LoadAsync(CancellationToken.None);

        foreach (var repository in new[] { a, b })
        {
            var ids = repository.GetAllElements().Select(e => (string?)e.Attribute("id")).ToArray();
            ids.ShouldContain(idA);
            ids.ShouldContain(idB);
            repository.HasPendingWrites.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_audited_change_commits_the_document_and_its_audit_together_or_nothing()
    {
        fixture.RequireCouchbase();
        var audited = new AuditedStore(fixture.Store, fixture.GameServer);
        var staffKey = new DocumentKey("gm_staff", "it-" + Guid.NewGuid().ToString("N")[..12]);
        var auditKey = new DocumentKey("gm_audit", "gm_audit::" + Guid.NewGuid().ToString("N"));

        await audited.RunAsync(async tx =>
        {
            await tx.InsertAsync(staffKey, new JsonObject { ["staffId"] = staffKey.Key });
            await tx.InsertAsync(auditKey, new JsonObject { ["operation"] = "staff.create" });
            return 0;
        });
        (await Kv.GetAsync(staffKey, CancellationToken.None)).ShouldNotBeNull();
        (await Kv.GetAsync(auditKey, CancellationToken.None)).ShouldNotBeNull();

        var unaudited = new DocumentKey("gm_staff", "it-" + Guid.NewGuid().ToString("N")[..12]);
        await Should.ThrowAsync<InvalidOperationException>(() => audited.RunAsync(async tx =>
        {
            await tx.InsertAsync(unaudited, new JsonObject { ["staffId"] = unaudited.Key });
            return 0;
        }));
        (await Kv.GetAsync(unaudited, CancellationToken.None)).ShouldBeNull();
    }
}
