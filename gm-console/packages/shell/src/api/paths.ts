/**
 * Path parameters of GM API calls (Phase A review carry-over): every value is `encodeURIComponent`-encoded into exactly one
 * path segment, and the values that a URL parser would treat as a dot segment (`.`, `..`) or that are empty are refused, so
 * no route or hash value can change which endpoint is called (`/players/../staff` would otherwise become `/staff`).
 */

const PATH_PARAMETER = /\{([^{}]+)\}/g;
const DOT_SEGMENT = /^(?:\.|%2e){1,2}$/i;

/** Thrown before any request is sent when a path parameter cannot be one safe segment. */
export class InvalidPathParameterError extends Error {
  readonly parameter: string;

  constructor(parameter: string, reason: string) {
    super(`Path parameter '${parameter}' ${reason}.`);
    this.name = 'InvalidPathParameterError';
    this.parameter = parameter;
  }
}

/** One encoded path segment for `value`, or an error for an empty, `.` or `..` value. */
export function encodePathSegment(parameter: string, value: unknown): string {
  if (typeof value !== 'string' && typeof value !== 'number') {
    throw new InvalidPathParameterError(parameter, 'is missing');
  }

  const encoded = encodeURIComponent(String(value));
  if (encoded === '' || DOT_SEGMENT.test(encoded)) {
    throw new InvalidPathParameterError(parameter, 'is empty or a dot segment');
  }

  return encoded;
}

/** `openapi-fetch` path serializer: `{name}` → one encoded segment (simple style only; the GM API uses no other). */
export function gmPathSerializer(pathname: string, pathParams: Record<string, unknown>): string {
  return pathname.replace(PATH_PARAMETER, (_match, name: string) => encodePathSegment(name, pathParams[name]));
}
