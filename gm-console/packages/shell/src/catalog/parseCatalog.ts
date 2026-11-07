/** One operation of the GM document as the console reads it at run time (data-model §8; GM-3 extends it). */
export interface GmOperation {
  /** `operationId` = the route name. */
  routeName: string;
  method: 'GET' | 'POST' | 'PUT' | 'DELETE';
  /** Path key, e.g. `/gm/v1/players/{playerId}`. */
  template: string;
  /** `x-gm-permission`: `public` or a permission. */
  permission: string;
  kind: 'command' | 'action' | 'query';
  target: 'player' | 'global';
  paged: boolean;
  summary?: string;
}

export interface GmCatalog {
  operations: ReadonlyMap<string, GmOperation>;
}

const METHODS = ['get', 'post', 'put', 'delete'] as const;
const KINDS: ReadonlySet<string> = new Set(['command', 'action', 'query']);
const TARGETS: ReadonlySet<string> = new Set(['player', 'global']);

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

/**
 * The catalog of a GM OpenAPI document (FR-065): every operation with an `operationId`, keyed by it; missing `x-gm-*` keys
 * default to kind `query`, target `global`, not paged, permission empty. Anything that is not an operation object is skipped.
 */
export function parseCatalog(document: unknown): GmCatalog {
  const operations = new Map<string, GmOperation>();
  const paths = record(record(document)?.paths) ?? {};
  for (const [template, item] of Object.entries(paths)) {
    const pathItem = record(item);
    if (!pathItem) {
      continue;
    }

    for (const method of METHODS) {
      const operation = record(pathItem[method]);
      const routeName = text(operation?.operationId);
      if (!operation || !routeName) {
        continue;
      }

      const kind = text(operation['x-gm-kind']);
      const target = text(operation['x-gm-target']);
      operations.set(routeName, {
        routeName,
        method: method.toUpperCase() as GmOperation['method'],
        template,
        permission: text(operation['x-gm-permission']) ?? '',
        kind: kind !== undefined && KINDS.has(kind) ? (kind as GmOperation['kind']) : 'query',
        target: target !== undefined && TARGETS.has(target) ? (target as GmOperation['target']) : 'global',
        paged: operation['x-gm-paged'] === true,
        summary: text(operation.summary),
      });
    }
  }

  return { operations };
}
