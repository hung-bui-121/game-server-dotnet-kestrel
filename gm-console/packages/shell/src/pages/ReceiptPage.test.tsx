import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { problemReply } from '../test/fakeFetch';
import { renderPage, type PageFixtureOptions } from '../test/renderPage';
import { ReceiptPage } from './ReceiptPage';

const renderReceipt = (routes: PageFixtureOptions['routes']) =>
  renderPage((v) => <ReceiptPage api={v.api} me={v.me} catalog={v.catalog} />, '/receipts/r-1', '/receipts/:receiptId', { routes });

describe('ReceiptPage (FR-070)', () => {
  it('shows the receipt as JSON text', async () => {
    const receipt = { receiptId: 'r-1', operation: 'economy.adjust', aggregates: [{ aggregateId: 'wallet', newRev: 3 }] };
    const { fake } = renderReceipt({ 'GET /gm/v1/receipts/r-1': { body: receipt } });

    expect((await screen.findByLabelText('Receipt')).textContent).toBe(JSON.stringify(receipt, null, 2));
    expect(fake.calls()).toEqual(['GET /gm/v1/receipts/r-1']);
  });

  it('says "receipt not found" on 404', async () => {
    renderReceipt({ 'GET /gm/v1/receipts/r-1': problemReply(404, 'RECEIPT_NOT_FOUND') });

    expect(await screen.findByRole('alert')).toHaveTextContent('Receipt not found.');
  });
});
