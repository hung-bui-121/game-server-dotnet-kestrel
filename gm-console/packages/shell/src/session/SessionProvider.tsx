import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { call, createCatalogClient, createGmClient } from '../api/client';
import { problemOf } from '../api/problem';
import { loadConfig } from '../config/loadConfig';
import type { GmBuiltFor, GmConsoleConfig, GmMe, GmProblem } from '../types';
import { createCsrfStore } from './csrf';
import { isWrongServer, toMe, type GmAuthMethods, type SessionServices, type SessionState, type SignedOutNotice } from './phases';
import { versionSkew } from './versions';

/** Hash routes the session navigates to. */
export const LOGIN_PATH = '/login';
export const HOME_PATH = '/';

/** SSO start route of the GM API (top-level navigation, design §6.5). */
export const SSO_START_PATH = '/gm/v1/auth/sso/start';

export interface SessionValue {
  state: SessionState;
  builtFor: GmBuiltFor;
  /** Local sign-in (FR-054): undefined on success, else the problem (401 = invalid staff id or password). */
  signInLocal: (staffId: string, password: string) => Promise<GmProblem | undefined>;
  /** Sign-out (FR-059): ends the server session, clears every in-memory state, shows the login page. */
  signOut: () => Promise<void>;
  /** Top-level navigation to the SSO start with the remembered hash as `returnUrl` (FR-053). */
  startSso: () => void;
  /** Starts again from `config.json` (error screens' retry). */
  restart: () => void;
  /** Keeps a console path in memory to return to after the next sign-in (FR-057; never put in the URL). */
  rememberReturnTo: (path: string) => void;
}

const SessionContext = createContext<SessionValue | undefined>(undefined);

export function useSession(): SessionValue {
  const value = useContext(SessionContext);
  if (!value) {
    throw new Error('useSession must be used inside SessionProvider.');
  }

  return value;
}

export interface SessionProviderProps {
  builtFor: GmBuiltFor;
  /** Tests inject a fake; defaults to the browser's fetch. */
  fetch?: typeof fetch;
  /** Top-level navigation (SSO start); tests inject a spy. */
  navigateTop?: (url: string) => void;
  children: ReactNode;
}

function browserFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  return globalThis.fetch(input, init);
}

function assignLocation(url: string): void {
  window.location.assign(url);
}

/** A console path that may be returned to: internal, absolute, not the login page. */
function isReturnPath(path: string): boolean {
  return path.startsWith('/') && !path.startsWith('//') && !path.startsWith(LOGIN_PATH);
}

/**
 * The session of the console (US6, data-model §7): config → `auth/methods` → `/me` → guard; local sign-in, SSO start,
 * sign-out; 401 → login keeping the hash in memory; 403 → one `/me` refresh per navigation. Nothing is stored in the browser.
 */
export function SessionProvider({ builtFor, fetch: fetchOverride, navigateTop = assignLocation, children }: SessionProviderProps) {
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const [state, setState] = useState<SessionState>({ phase: 'loading-config' });
  const current = useRef<SessionState>(state);
  const returnTo = useRef<string | undefined>(undefined);
  const here = useRef<string>(HOME_PATH);
  const forbiddenRefreshFor = useRef<string | undefined>(undefined);
  const signingOut = useRef(false);
  const fetchFn = fetchOverride ?? browserFetch;

  useEffect(() => {
    here.current = location.pathname + location.search;
  }, [location]);

  const update = useCallback((next: SessionState) => {
    current.current = next;
    setState(next);
  }, []);

  const enterSession = useCallback(
    (services: SessionServices, methods: GmAuthMethods, me: GmMe, returnAfter: boolean) => {
      const skew = versionSkew(builtFor, me);
      if (isWrongServer(services.config, me)) {
        update({ phase: 'blocked', services, methods, me, skew });
        return;
      }

      let landing: string | undefined;
      if (returnAfter) {
        landing = returnTo.current ?? HOME_PATH;
        returnTo.current = undefined;
      }

      // The gate leaves the login page for `landing` (no navigation here: one owner of that redirect).
      update({ phase: 'signed-in', services, methods, me, skew, landing });
    },
    [builtFor, update],
  );

  const onUnauthorized = useCallback(() => {
    const session = current.current;
    if (signingOut.current || (session.phase !== 'signed-in' && session.phase !== 'blocked')) {
      return;
    }

    if (isReturnPath(here.current)) {
      returnTo.current = here.current;
    }

    session.services.csrf.clear();
    queryClient.clear();
    forbiddenRefreshFor.current = undefined;
    update({ phase: 'signed-out', services: session.services, methods: session.methods, notice: 'expired' });
    void navigate(LOGIN_PATH, { replace: true });
  }, [navigate, queryClient, update]);

  const onForbidden = useCallback(() => {
    const session = current.current;
    if (session.phase !== 'signed-in' || forbiddenRefreshFor.current === here.current) {
      return;
    }

    forbiddenRefreshFor.current = here.current;
    void call(session.services.api.GET('/gm/v1/me'))
      .then((me) => {
        const latest = current.current;
        if (latest.phase === 'signed-in' && latest.services === session.services) {
          enterSession(session.services, session.methods, toMe(me), false);
        }
      })
      .catch(() => undefined);
  }, [enterSession]);

  const createServices = useCallback(
    (config: GmConsoleConfig): SessionServices => {
      const csrf = createCsrfStore(async () => (await call(api.GET('/gm/v1/auth/csrf'))).token);
      const options = { config, fetch: fetchFn, csrf, onUnauthorized, onForbidden };
      const api = createGmClient(options);
      return { config, api, catalog: createCatalogClient(options), csrf };
    },
    [fetchFn, onForbidden, onUnauthorized],
  );

  const start = useCallback(
    async (isActive: () => boolean) => {
      const loaded = await loadConfig(fetchFn);
      if (!isActive()) {
        return;
      }

      if (!loaded.ok) {
        update({ phase: 'config-error', field: loaded.field, message: loaded.message });
        return;
      }

      const services = createServices(loaded.config);
      update({ phase: 'checking', services });
      let methods: GmAuthMethods;
      try {
        methods = await call(services.api.GET('/gm/v1/auth/methods'));
      } catch (error) {
        if (isActive()) {
          update({ phase: 'api-error', services, problem: problemOf(error) });
        }

        return;
      }

      try {
        const me = toMe(await call(services.api.GET('/gm/v1/me')));
        if (isActive()) {
          enterSession(services, methods, me, false);
        }
      } catch (error) {
        const problem = problemOf(error);
        if (isActive()) {
          update(problem.status === 401 ? { phase: 'signed-out', services, methods } : { phase: 'api-error', services, problem });
        }
      }
    },
    [createServices, enterSession, fetchFn, update],
  );

  const run = useRef(0);
  const launch = useCallback(() => {
    const id = ++run.current;
    void start(() => run.current === id);
  }, [start]);

  useEffect(() => {
    launch();
    return () => {
      run.current += 1;
    };
  }, [launch]);

  const restart = useCallback(() => {
    update({ phase: 'loading-config' });
    launch();
  }, [launch, update]);

  const signInLocal = useCallback(
    async (staffId: string, password: string): Promise<GmProblem | undefined> => {
      const session = current.current;
      if (session.phase !== 'signed-out') {
        return undefined;
      }

      try {
        await session.services.csrf.refresh();
        const me = toMe(await call(session.services.api.POST('/gm/v1/auth/login', { body: { staffId, password } })));
        // The anonymous token is not valid for the new session; the next mutation fetches one (the middleware also retries
        // once on CSRF_FAILED). Fetching it here could fail after the server session exists and hide it behind the login
        // page (review T068 m3).
        session.services.csrf.clear();
        enterSession(session.services, session.methods, me, true);
        return undefined;
      } catch (error) {
        return problemOf(error);
      }
    },
    [enterSession],
  );

  const signOut = useCallback(async () => {
    const session = current.current;
    if (session.phase !== 'signed-in' && session.phase !== 'blocked') {
      return;
    }

    signingOut.current = true;
    let notice: SignedOutNotice = 'signed-out';
    try {
      await call(session.services.api.POST('/gm/v1/auth/logout'));
    } catch (error) {
      if (problemOf(error).status !== 401) {
        notice = 'sign-out-unconfirmed';
      }
    } finally {
      signingOut.current = false;
    }

    session.services.csrf.clear();
    queryClient.clear();
    returnTo.current = undefined;
    forbiddenRefreshFor.current = undefined;
    update({ phase: 'signed-out', services: session.services, methods: session.methods, notice });
    void navigate(LOGIN_PATH, { replace: true });
    void session.services.csrf.refresh().catch(() => undefined);
  }, [navigate, queryClient, update]);

  const startSso = useCallback(() => {
    const session = current.current;
    if (session.phase !== 'signed-out') {
      return;
    }

    const returnUrl = '/#' + (returnTo.current ?? HOME_PATH);
    navigateTop(`${session.services.config.apiBaseUrl}${SSO_START_PATH}?returnUrl=${encodeURIComponent(returnUrl)}`);
  }, [navigateTop]);

  const rememberReturnTo = useCallback((path: string) => {
    if (isReturnPath(path)) {
      returnTo.current = path;
    }
  }, []);

  const value = useMemo<SessionValue>(
    () => ({ state, builtFor, signInLocal, signOut, startSso, restart, rememberReturnTo }),
    [state, builtFor, signInLocal, signOut, startSso, restart, rememberReturnTo],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
