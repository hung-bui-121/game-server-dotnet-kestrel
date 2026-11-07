import type { ReactElement } from 'react';
import { useLocation } from 'react-router';
import { vi } from 'vitest';
import { createGmClient } from '../api/client';
import { parseCatalog } from '../catalog/parseCatalog';
import { builtinRegistry } from '../navigation/builtins';
import { ConsoleContext, type ConsoleValue } from '../navigation/ConsoleContext';
import { toMe } from '../session/phases';
import { createFakeFetch, type FakeRoute } from './fakeFetch';
import { ALL_PERMISSIONS, catalogDocument, CONFIG, meBody, READ_ROUTES } from './fixtures';
import { renderRoutes } from './render';

export interface PageFixtureOptions {
  routes?: Record<string, FakeRoute>;
  permissions?: string[];
  /** Operations of the catalog (default: every framework read route). */
  operations?: Record<string, [string, string, string, Record<string, unknown>?]>;
}

/**
 * A ready console around a page: typed client over a fake fetch whose CSRF source fails (no page may mutate in GM-2, D10),
 * staff member, catalog and the built-in registry.
 */
export function pageFixture({ routes = {}, permissions = ALL_PERMISSIONS, operations = READ_ROUTES }: PageFixtureOptions = {}) {
  const fake = createFakeFetch(routes);
  const noMutation = () => Promise.reject(new Error('GM-2 pages never mutate (D10).'));
  const api = createGmClient({
    config: CONFIG,
    fetch: fake.fetch,
    csrf: { get: noMutation, refresh: noMutation },
    onUnauthorized: vi.fn(),
    onForbidden: vi.fn(),
  });
  const value: ConsoleValue = {
    config: CONFIG,
    api,
    me: toMe(meBody({ permissions })),
    catalog: parseCatalog(catalogDocument(operations)),
    registry: builtinRegistry(),
  };
  return { fake, value };
}

function Elsewhere() {
  const location = useLocation();
  return <div data-testid="elsewhere">{location.pathname}</div>;
}

/** Renders a page element at `path` (route `pattern`) inside the console context; other routes show their path. */
export function renderPage(element: (value: ConsoleValue) => ReactElement, path: string, pattern: string, options: PageFixtureOptions = {}) {
  const { fake, value } = pageFixture(options);
  const view = renderRoutes(
    [
      { path: pattern, element: <ConsoleContext.Provider value={value}>{element(value)}</ConsoleContext.Provider> },
      { path: '*', element: <Elsewhere /> },
    ],
    path,
  );
  return { fake, value, view };
}
