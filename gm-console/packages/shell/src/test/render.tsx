import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router';

/** A query client like the console's (no automatic retry), fresh per test. */
export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false }, mutations: { retry: false } } });
}

export function TestProviders({ client, children }: { client: QueryClient; children: ReactNode }) {
  return (
    <MantineProvider env="test" withGlobalClasses={false} withCssVariables={false}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MantineProvider>
  );
}

export interface RenderRoutesResult extends RenderResult {
  router: ReturnType<typeof createMemoryRouter>;
  client: QueryClient;
  /** Current router path + search. */
  path(): string;
}

/** Renders routes in a memory router with Mantine and TanStack Query. */
export function renderRoutes(routes: RouteObject[], initialPath = '/', client = createTestQueryClient()): RenderRoutesResult {
  const router = createMemoryRouter(routes, { initialEntries: [initialPath] });
  const result = render(
    <TestProviders client={client}>
      <RouterProvider router={router} />
    </TestProviders>,
  );
  return {
    ...result,
    router,
    client,
    path: () => router.state.location.pathname + router.state.location.search,
  };
}

/** Renders one element at `path` matched by `pattern` (e.g. `/players/:playerId`). */
export function renderAt(element: ReactElement, path = '/', pattern = '*', client?: QueryClient): RenderRoutesResult {
  return renderRoutes([{ path: pattern, element }, { path: '*', element: <div>elsewhere</div> }], path, client);
}
