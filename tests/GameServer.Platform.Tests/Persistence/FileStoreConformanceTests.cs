using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>
/// Slice 011 T018: the store conformance suite on the file store, unchanged (FR-018, SC-003, US2-3), plus the reopen
/// invariant after every case: a new store on the same folder holds exactly the last committed state, so a write path that
/// forgets to persist fails here.
/// </summary>
public sealed class FileStoreConformanceTests : StoreConformanceTests, IDisposable
{
    private readonly FileStoreTestFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FileDocumentStore _store;

    public FileStoreConformanceTests() => _store = _folder.Open(_clock);

    protected override ITransactionalDocumentStore Store => _store.Store;

    public void Dispose()
    {
        try
        {
            var committed = _folder.Snapshot(_store.Store, Docs, "receipts");
            _store.Dispose();
            using var reopened = _folder.Open(_clock);
            _folder.Snapshot(reopened.Store, Docs, "receipts").ShouldBe(committed);
        }
        finally
        {
            _store.Dispose();
            _folder.Dispose();
        }
    }
}
