import type { GmCatalogClient, GmClient } from '../api/client';
import type { components } from '../generated/gm-api';
import type { GmConsoleConfig, GmMe, GmProblem } from '../types';
import type { CsrfStore } from './csrf';
import type { VersionSkew } from './versions';

export type GmAuthMethods = components['schemas']['GmAuthMethods'];
type GmMeResponse = components['schemas']['GmMeResponse'];

/** What a loaded configuration gives the session: the API clients and the token store. */
export interface SessionServices {
  config: GmConsoleConfig;
  api: GmClient;
  catalog: GmCatalogClient;
  csrf: CsrfStore;
}

/** Why the login page shows (a message, never a reason from the URL). */
export type SignedOutNotice = 'expired' | 'signed-out' | 'sign-out-unconfirmed';

/**
 * Session phases (data-model §7). `api-error` = the start calls got no usable answer (never shown as signed out, FR-062);
 * `ready` and `catalog-error` are the console phases derived from the catalog query of a `signed-in` session.
 */
export type SessionState =
  | { phase: 'loading-config' }
  | { phase: 'config-error'; field: string; message: string }
  | { phase: 'checking'; services: SessionServices }
  | { phase: 'api-error'; services: SessionServices; problem: GmProblem }
  | { phase: 'signed-out'; services: SessionServices; methods: GmAuthMethods; notice?: SignedOutNotice }
  | {
      phase: 'signed-in';
      services: SessionServices;
      methods: GmAuthMethods;
      me: GmMe;
      skew: VersionSkew;
      /** After a sign-in on the login page: the console path to open (the page kept in memory, else home). */
      landing?: string;
    }
  | { phase: 'blocked'; services: SessionServices; methods: GmAuthMethods; me: GmMe; skew: VersionSkew };

export type SessionPhase = SessionState['phase'];

export type ConsolePhase = Exclude<SessionPhase, 'signed-in'> | 'catalog-loading' | 'ready' | 'catalog-error';

/** The console phase of a session and its catalog query. */
export function consolePhase(phase: SessionPhase, catalog: 'pending' | 'error' | 'success'): ConsolePhase {
  if (phase !== 'signed-in') {
    return phase;
  }

  return catalog === 'success' ? 'ready' : catalog === 'error' ? 'catalog-error' : 'catalog-loading';
}

/** `/me` answer → `GmMe` (permissions as a set; missing versions as empty text). */
export function toMe(response: GmMeResponse): GmMe {
  return {
    staffId: response.staffId,
    displayName: response.displayName,
    gameId: response.gameId,
    environmentId: response.environmentId,
    permissions: new Set(response.permissions),
    frameworkVersion: response.frameworkVersion ?? '',
    gameVersion: response.gameVersion ?? '',
  };
}

/** Q19 guard: the console blocks when `/me` names another game or environment than its `config.json` (FR-055). */
export function isWrongServer(config: GmConsoleConfig, me: GmMe): boolean {
  return me.gameId !== config.gameId || me.environmentId !== config.environmentId;
}
