import { Button, Loader, Stack, Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { problemOf } from '../api/problem';
import { ErrorDisplay } from './ErrorDisplay';

/** Page size of every GM list read (FR-073, GM-06). */
export const PAGE_SIZE = 50;

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export interface PagedListProps<T> {
  /** Query key of the list (one cache entry per list and filter, cleared on sign-out). */
  queryKey: readonly unknown[];
  /** One page; `cursor` is undefined for the first page. */
  fetchPage: (cursor: string | undefined) => Promise<Page<T>>;
  renderItems: (items: T[]) => ReactNode;
  emptyText?: string;
}

/**
 * A cursor-paged list (FR-073): first page, then "Load more" with the last `nextCursor` until it is null. A failed later page
 * keeps the loaded items and shows the error next to the button, which retries.
 */
export function PagedList<T>({ queryKey, fetchPage, renderItems, emptyText = 'Nothing to show.' }: PagedListProps<T>) {
  const query = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) => fetchPage(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  if (query.isPending) {
    return <Loader size="sm" aria-label="Loading" />;
  }

  if (query.isError && !query.isFetchNextPageError) {
    return <ErrorDisplay problem={problemOf(query.error)} />;
  }

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];
  return (
    <Stack gap="sm">
      {items.length === 0 ? <Text size="sm">{emptyText}</Text> : renderItems(items)}
      {query.isFetchNextPageError && <ErrorDisplay problem={problemOf(query.error)} />}
      {query.hasNextPage && (
        <Button variant="default" w="fit-content" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
          Load more
        </Button>
      )}
    </Stack>
  );
}
