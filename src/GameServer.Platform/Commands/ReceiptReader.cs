using GameServer.Platform.Persistence;

namespace GameServer.Platform.Commands;

/// <summary>Reads committed receipts for lookup (CMD-09). Ownership checks belong to the caller.</summary>
public sealed class ReceiptReader(ITransactionalDocumentStore store)
{
    public async Task<ReceiptDocument?> GetAsync(string receiptId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(ReceiptDocument.KeyFor(receiptId), cancellationToken);
        return document is null ? null : ReceiptDocument.FromJson(document.Content);
    }
}
