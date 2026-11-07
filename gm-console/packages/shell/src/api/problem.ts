import type { GmProblem } from '../types';

/** Exposed by the GM API on every answer (slice 018 FR-001); the fallback request id of an error (FR-062). */
export const REQUEST_ID_HEADER = 'X-Request-Id';

/** `403` code of a failed CSRF check (token or origin): the client refreshes the token and retries a mutation once (FR-052). */
export const CSRF_FAILED = 'CSRF_FAILED';

/** Client-side codes (never sent by the server). */
export const NETWORK_ERROR = 'NETWORK_ERROR';
export const INVALID_PATH_PARAMETER = 'INVALID_PATH_PARAMETER';
export const UNEXPECTED_ERROR = 'UNEXPECTED_ERROR';
/** A mutation the console of this slice must not send (GM-2 boundary D10); refused before any request. */
export const MUTATION_NOT_ALLOWED = 'MUTATION_NOT_ALLOWED';

/** Status of a problem without a readable answer (network failure, CORS refusal, request never sent). */
export const NO_ANSWER_STATUS = 0;

/** The error thrown by API calls: carries the GM problem (TanStack Query errors are `GmApiError`). */
export class GmApiError extends Error {
  readonly problem: GmProblem;

  constructor(problem: GmProblem) {
    super(`${problem.code} (${problem.status})`);
    this.name = 'GmApiError';
    this.problem = problem;
  }
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function fieldErrors(value: unknown): Record<string, string[]> {
  const record = asRecord(value);
  if (!record) {
    return {};
  }

  const errors: Record<string, string[]> = {};
  for (const [field, messages] of Object.entries(record)) {
    if (Array.isArray(messages)) {
      errors[field] = messages.filter((m): m is string => typeof m === 'string');
    }
  }

  return errors;
}

/** Problem Details of an error answer; the request id comes from the body, else the exposed `X-Request-Id` header. */
export function problemFromResponse(response: Response, body: unknown): GmProblem {
  const record = asRecord(body) ?? {};
  const text = (key: string): string | undefined => (typeof record[key] === 'string' ? record[key] : undefined);

  return {
    status: response.status,
    code: text('code') ?? `HTTP_${response.status}`,
    codeNumber: typeof record.codeNumber === 'number' ? record.codeNumber : undefined,
    title: text('title'),
    detail: text('detail'),
    retryable: record.retryable === true,
    errors: fieldErrors(record.errors),
    requestId: text('requestId') ?? response.headers.get(REQUEST_ID_HEADER) ?? undefined,
  };
}

/** A failure without a readable answer: the API is down, unreachable, or refused by CORS (FR-062). */
export function networkProblem(apiBaseUrl: string, cause?: unknown): GmProblem {
  return {
    status: NO_ANSWER_STATUS,
    code: NETWORK_ERROR,
    retryable: true,
    errors: {},
    detail: `No answer from the GM API at ${originOf(apiBaseUrl)}.`,
    title: cause instanceof Error ? cause.message : undefined,
  };
}

/** The GM problem of any thrown value. */
export function problemOf(error: unknown): GmProblem {
  if (error instanceof GmApiError) {
    return error.problem;
  }

  return {
    status: NO_ANSWER_STATUS,
    code: UNEXPECTED_ERROR,
    retryable: false,
    errors: {},
    detail: error instanceof Error ? error.message : String(error),
  };
}

/** `scheme://host[:port]` of a URL, or the text itself when it does not parse. */
export function originOf(url: string): string {
  try {
    return new URL(url).origin;
  } catch {
    return url;
  }
}
