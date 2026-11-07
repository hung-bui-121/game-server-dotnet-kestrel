import { MantineProvider, type MantineColorSchemeManager } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { createHashRouter, RouterProvider, type RouteObject } from 'react-router';
import { builtinRegistry } from './navigation/builtins';
import type { RegistryContents } from './navigation/registry';
import { createConsoleRoutes } from './router';
import type { GmBuiltFor } from './types';

/**
 * Mantine's default colour-scheme manager uses `localStorage`; the console keeps nothing in browser storage (FR-061), so the
 * scheme lives in memory (light, the default).
 */
export const memoryColorSchemeManager: MantineColorSchemeManager = {
  get: (defaultValue) => defaultValue,
  set: () => undefined,
  subscribe: () => undefined,
  unsubscribe: () => undefined,
  clear: () => undefined,
};

/** Reads are not retried automatically (a 401/403 must not loop; commands are never retried, design §12.8). */
export function createConsoleQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
}

type ConsoleRouter = ReturnType<typeof createHashRouter>;

export interface GmConsoleAppProps {
  builtFor: GmBuiltFor;
  fetch?: typeof fetch;
  navigateTop?: (url: string) => void;
  registry?: RegistryContents;
  /** Tests use a memory router; the console uses the hash router (A6). */
  createRouter?: (routes: RouteObject[]) => ConsoleRouter;
}

/**
 * The console: Mantine without any injected `<style>` element (V3: the static host keeps `style-src 'self'`): no global classes,
 * no runtime CSS variables (the default theme's variables are in the static `styles.css`; a custom theme would need its
 * variables in a static CSS file), no responsive style props; in-memory colour scheme; TanStack Query; the hash router.
 */
export function GmConsoleApp({ builtFor, fetch, navigateTop, registry, createRouter = createHashRouter }: GmConsoleAppProps) {
  const [queryClient] = useState(createConsoleQueryClient);
  const [router] = useState(() =>
    createRouter(createConsoleRoutes({ builtFor, fetch, navigateTop, registry: registry ?? builtinRegistry() })),
  );

  return (
    <MantineProvider
      colorSchemeManager={memoryColorSchemeManager}
      defaultColorScheme="light"
      withGlobalClasses={false}
      withCssVariables={false}
    >
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>
  );
}
