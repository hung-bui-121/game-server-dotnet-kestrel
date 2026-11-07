import { Alert, Button, Loader, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useMemo, type ReactNode } from 'react';
import { Navigate, Outlet, useMatch, type RouteObject } from 'react-router';
import { call, CATALOG_PATH } from './api/client';
import { problemOf } from './api/problem';
import { parseCatalog } from './catalog/parseCatalog';
import { BlockScreen } from './components/BlockScreen';
import { ErrorDisplay } from './components/ErrorDisplay';
import { Layout } from './components/Layout';
import { ConsoleContext, useConsole, type ConsoleValue } from './navigation/ConsoleContext';
import { Menu } from './navigation/Menu';
import { isVisible, menuGroups, type GmPageDef, type RegistryContents } from './navigation/registry';
import { LoginPage } from './pages/LoginPage';
import { NotAvailable } from './pages/NotAvailable';
import { RedirectToLogin } from './session/RedirectToLogin';
import { HOME_PATH, LOGIN_PATH, SessionProvider, useSession, type SessionProviderProps } from './session/SessionProvider';
import type { SessionServices } from './session/phases';
import type { VersionSkew } from './session/versions';
import type { GmMe } from './types';

/** Query key of the catalog: loaded once per session (`staleTime: Infinity`), removed with every query on 401/sign-out (N4). */
export const CATALOG_QUERY_KEY = ['gm', 'catalog'] as const;

/** The page shown for `#/` and unknown hashes: Players when visible (data-model §9). */
export const PREFERRED_HOME = '/players';

function Centered({ children }: { children: ReactNode }) {
  return (
    <main className="gm-center">
      <Stack maw="40rem" w="100%">
        {children}
      </Stack>
    </main>
  );
}

function Loading() {
  return (
    <Centered>
      <Loader aria-label="Loading" />
    </Centered>
  );
}

/** The ready console: catalog (once), then layout, menu and the matched page. */
function CatalogGate({ services, me, skew, registry }: { services: SessionServices; me: GmMe; skew: VersionSkew; registry: RegistryContents }) {
  const { signOut } = useSession();
  const catalogQuery = useQuery({
    queryKey: CATALOG_QUERY_KEY,
    queryFn: async () => parseCatalog(await call(services.catalog.GET(CATALOG_PATH))),
    staleTime: Infinity,
    gcTime: Infinity,
  });
  const catalog = catalogQuery.data;
  const value = useMemo<ConsoleValue | undefined>(
    () => (catalog ? { config: services.config, api: services.api, me, catalog, registry } : undefined),
    [catalog, me, registry, services],
  );

  if (catalogQuery.isPending) {
    return <Loading />;
  }

  if (!value) {
    return (
      <Centered>
        <ErrorDisplay problem={problemOf(catalogQuery.error)} title="The GM catalog could not be loaded" />
        <Button w="fit-content" onClick={() => void catalogQuery.refetch()}>
          Retry
        </Button>
        <Button w="fit-content" variant="default" onClick={() => void signOut()}>
          Sign out
        </Button>
      </Centered>
    );
  }

  return (
    <ConsoleContext.Provider value={value}>
      <Layout me={me} skew={skew} navigation={<Menu groups={menuGroups(registry.pages, me, value.catalog)} />} onSignOut={() => void signOut()}>
        <Outlet />
      </Layout>
    </ConsoleContext.Provider>
  );
}

/** The session gate (FR-050, FR-055, FR-057): every phase decides what renders before any page can call the API. */
function SessionGate({ registry }: { registry: RegistryContents }) {
  const { state, restart, signOut } = useSession();
  const atLogin = useMatch(LOGIN_PATH) !== null;

  switch (state.phase) {
    case 'loading-config':
    case 'checking':
      return <Loading />;
    case 'config-error':
      return (
        <Centered>
          <Alert color="red" title="The console configuration is invalid" role="alert">
            <Text size="sm">Field: {state.field}</Text>
            <Text size="sm">{state.message}</Text>
          </Alert>
          <Button w="fit-content" onClick={restart}>
            Retry
          </Button>
        </Centered>
      );
    case 'api-error':
      return (
        <Centered>
          <ErrorDisplay problem={state.problem} />
          <Button w="fit-content" onClick={restart}>
            Retry
          </Button>
        </Centered>
      );
    case 'signed-out':
      return atLogin ? <Outlet /> : <RedirectToLogin />;
    case 'blocked':
      return <BlockScreen config={state.services.config} me={state.me} onSignOut={() => void signOut()} />;
    case 'signed-in':
      return atLogin ? (
        <Navigate to={state.landing ?? HOME_PATH} replace />
      ) : (
        <CatalogGate services={state.services} me={state.me} skew={state.skew} registry={registry} />
      );
  }
}

/** A registered page: rendered only when visible; otherwise "not available" with zero API calls (FR-066). */
function PageHost({ def }: { def: GmPageDef }) {
  const { api, me, catalog } = useConsole();
  if (!isVisible(def, me, catalog)) {
    return <NotAvailable />;
  }

  const Page = def.component;
  return <Page api={api} me={me} catalog={catalog} />;
}

/** `#/` and unknown hashes: Players when visible, else the first visible menu page, else a message. */
function Home() {
  const { me, catalog, registry } = useConsole();
  const visible = registry.pages.filter((page) => !page.path.includes(':') && isVisible(page, me, catalog));
  const target = visible.find((page) => page.path === PREFERRED_HOME) ?? visible[0];
  if (!target) {
    return (
      <Alert color="gray" title="Nothing to show" role="alert">
        Your permissions do not include any page of this console. Ask a GM administrator for access.
      </Alert>
    );
  }

  return <Navigate to={target.path} replace />;
}

export type ConsoleRouteOptions = Omit<SessionProviderProps, 'children'> & { registry: RegistryContents };

/** Hash routes of the console (plan US7): login, every registered page, home and fallback. */
export function createConsoleRoutes({ registry, ...session }: ConsoleRouteOptions): RouteObject[] {
  return [
    {
      element: (
        <SessionProvider {...session}>
          <SessionGate registry={registry} />
        </SessionProvider>
      ),
      children: [
        { path: LOGIN_PATH, element: <LoginPage /> },
        {
          children: [
            { index: true, element: <Home /> },
            ...registry.pages.map((def) => ({ path: def.path, element: <PageHost def={def} /> })),
            { path: '*', element: <Home /> },
          ],
        },
      ],
    },
  ];
}
