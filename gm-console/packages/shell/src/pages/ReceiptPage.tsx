import { Stack, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router';
import { call } from '../api/client';
import { JsonView } from '../components/JsonView';
import { QueryState } from '../components/QueryState';
import type { GmPageProps } from '../navigation/registry';

/** A receipt (`gm.receipt`, FR-070) as JSON text; reached from a player's receipts tab. */
export function ReceiptPage({ api }: GmPageProps) {
  const { receiptId = '' } = useParams();
  const query = useQuery({
    queryKey: ['gm', 'receipt', receiptId],
    queryFn: () => call(api.GET('/gm/v1/receipts/{receiptId}', { params: { path: { receiptId } } })),
  });

  return (
    <Stack>
      <Title order={1} size="h2">
        Receipt {receiptId}
      </Title>
      <QueryState query={query} notFound="Receipt not found.">
        {(receipt) => <JsonView value={receipt} label="Receipt" />}
      </QueryState>
    </Stack>
  );
}
