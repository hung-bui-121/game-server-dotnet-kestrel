import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { call } from '../api/client';
import { problemReply } from '../test/fakeFetch';
import { renderPage } from '../test/renderPage';
import { PAGE_SIZE, PagedList } from './PagedList';

describe('PagedList (FR-073, US7-4)', () => {
  it('loads pages of 50 with the last cursor, keeps loaded rows when a page fails, and stops when nextCursor is null', async () => {
    let second = 0;
    const { fake } = renderPage(
      ({ api }) => (
        <PagedList<{ staffId: string }>
          queryKey={['test', 'staff']}
          fetchPage={(cursor) => call(api.GET('/gm/v1/staff', { params: { query: { pageSize: PAGE_SIZE, cursor } } }))}
          renderItems={(items) => (
            <ul aria-label="rows">
              {items.map((i) => (
                <li key={i.staffId}>{i.staffId}</li>
              ))}
            </ul>
          )}
        />
      ),
      '/',
      '/',
      {
        routes: {
          'GET /gm/v1/staff': (request) => {
            if (!request.search.includes('cursor=')) {
              return { body: { items: [{ staffId: 'a' }, { staffId: 'b' }], nextCursor: 'c-1' } };
            }

            second += 1;
            return second === 1 ? problemReply(503, 'UNAVAILABLE') : { body: { items: [{ staffId: 'c' }], nextCursor: null } };
          },
        },
      },
    );

    expect(await screen.findByText('b')).toBeInTheDocument();
    expect(fake.requests[0]?.search).toBe('?pageSize=50');

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('UNAVAILABLE');
    expect(screen.getAllByRole('listitem').map((i) => i.textContent)).toEqual(['a', 'b']);
    expect(fake.requests[1]?.search).toBe('?pageSize=50&cursor=c-1');

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getAllByRole('listitem').map((i) => i.textContent)).toEqual(['a', 'b', 'c']));
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
    expect(fake.requests).toHaveLength(3);
  });

  it('shows the error of a failed first page and the empty text of an empty list', async () => {
    renderPage(
      ({ api }) => (
        <>
          <PagedList
            queryKey={['test', 'fail']}
            fetchPage={() => call(api.GET('/gm/v1/staff', { params: { query: { pageSize: PAGE_SIZE } } }))}
            renderItems={() => null}
          />
          <PagedList
            queryKey={['test', 'empty']}
            fetchPage={() => Promise.resolve({ items: [], nextCursor: null })}
            renderItems={() => null}
            emptyText="Nothing here."
          />
        </>
      ),
      '/',
      '/',
      { routes: { 'GET /gm/v1/staff': problemReply(403, 'FORBIDDEN') } },
    );

    expect(await screen.findByRole('alert')).toHaveTextContent('FORBIDDEN');
    expect(await screen.findByText('Nothing here.')).toBeInTheDocument();
  });
});
