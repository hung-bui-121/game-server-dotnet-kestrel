import type { GmCsrf } from '../api/client';

/** The antiforgery token, in memory only (FR-052, FR-061): fetched before the first mutation, refreshed after sign-in/out. */
export interface CsrfStore extends GmCsrf {
  /** Forgets the token and any request in flight (401, sign-in, sign-out); the next mutation fetches a new one. */
  clear(): void;
}

/**
 * One token at a time. Concurrent `get()` calls share one request; concurrent `refresh()` calls share one refresh (review T068
 * m2); `clear()` drops both, so a token requested before a sign-in or sign-out is never stored or handed out after it.
 */
export function createCsrfStore(fetchToken: () => Promise<string>): CsrfStore {
  let token: string | undefined;
  let pending: Promise<string> | undefined;
  let refreshing: Promise<string> | undefined;
  let generation = 0;

  const load = (): Promise<string> => {
    if (pending) {
      return pending;
    }

    const started = generation;
    const request = fetchToken()
      .then((value) => {
        if (started === generation) {
          token = value;
        }

        return value;
      })
      .finally(() => {
        if (pending === request) {
          pending = undefined;
        }
      });
    pending = request;
    return request;
  };

  const forget = () => {
    generation += 1;
    token = undefined;
    pending = undefined;
    refreshing = undefined;
  };

  return {
    get: () => (token !== undefined ? Promise.resolve(token) : load()),
    refresh: () => {
      if (refreshing) {
        return refreshing;
      }

      forget();
      const request = load().finally(() => {
        if (refreshing === request) {
          refreshing = undefined;
        }
      });
      refreshing = request;
      return request;
    },
    clear: forget,
  };
}
