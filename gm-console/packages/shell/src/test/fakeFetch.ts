/**
 * A fake `fetch` for unit and component tests (slice 018 D16: no MSW, no network). Routes are `METHOD path` keys (path without
 * query, decoded as the server would see it; the full URL is recorded). A route answers with a static reply or a function of
 * the recorded request; replies are consumed in order when a route is given an array (the last one repeats).
 */

export interface RecordedRequest {
  method: string;
  url: string;
  /** Pathname as sent (still percent-encoded). */
  path: string;
  search: string;
  headers: Record<string, string>;
  credentials: RequestCredentials | undefined;
  cache: RequestCache | undefined;
  body: string | undefined;
}

export interface FakeReply {
  status?: number;
  headers?: Record<string, string>;
  body?: unknown;
  /** Reject the fetch (network failure / CORS refusal). */
  networkError?: boolean;
}

export type FakeRoute = FakeReply | FakeReply[] | ((request: RecordedRequest) => FakeReply | Promise<FakeReply>);

export interface FakeFetch {
  fetch: typeof fetch;
  requests: RecordedRequest[];
  /** `METHOD path` of every request, in order. */
  calls(): string[];
  route(key: string, reply: FakeRoute): void;
}

function headersOf(headers: Headers): Record<string, string> {
  const result: Record<string, string> = {};
  headers.forEach((value, name) => {
    result[name.toLowerCase()] = value;
  });
  return result;
}

export function createFakeFetch(routes: Record<string, FakeRoute> = {}): FakeFetch {
  const table = new Map<string, FakeRoute>(Object.entries(routes));
  const used = new Map<string, number>();
  const requests: RecordedRequest[] = [];

  const fetchFn = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const body = request.method === 'GET' || request.method === 'HEAD' ? undefined : await request.clone().text();
    const recorded: RecordedRequest = {
      method: request.method,
      url: request.url,
      path: url.pathname,
      search: url.search,
      headers: headersOf(request.headers),
      credentials: input instanceof Request ? request.credentials : init?.credentials,
      cache: input instanceof Request ? request.cache : init?.cache,
      body: body === '' ? undefined : body,
    };
    requests.push(recorded);

    const key = `${request.method} ${url.pathname}`;
    const route = table.get(key);
    if (route === undefined) {
      return new Response(JSON.stringify({ code: 'NOT_FOUND', title: `fake: no route ${key}` }), {
        status: 404,
        headers: { 'Content-Type': 'application/problem+json' },
      });
    }

    let reply: FakeReply;
    if (typeof route === 'function') {
      reply = await route(recorded);
    } else if (Array.isArray(route)) {
      const index = used.get(key) ?? 0;
      used.set(key, index + 1);
      reply = route[Math.min(index, route.length - 1)] ?? {};
    } else {
      reply = route;
    }

    if (reply.networkError) {
      throw new TypeError('Failed to fetch');
    }

    const status = reply.status ?? 200;
    const noBody = status === 204 || status === 304 || reply.body === undefined;
    return new Response(noBody ? null : JSON.stringify(reply.body), {
      status,
      headers: { ...(noBody ? {} : { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' }), ...reply.headers },
    });
  };

  return {
    fetch: fetchFn,
    requests,
    calls: () => requests.map((r) => `${r.method} ${r.path}`),
    route: (key, reply) => {
      table.set(key, reply);
      used.delete(key);
    },
  };
}

/** A Problem Details reply. */
export function problemReply(status: number, code: string, extra: Record<string, unknown> = {}): FakeReply {
  return {
    status,
    headers: { 'X-Request-Id': `rid-${code.toLowerCase()}` },
    body: { title: code, status, code, codeNumber: status * 10, requestId: `rid-${code.toLowerCase()}`, retryable: false, ...extra },
  };
}
