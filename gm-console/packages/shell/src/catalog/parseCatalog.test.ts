import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseCatalog } from './parseCatalog';

/** The committed framework GM document (slice 017), read from disk as the test input. */
const documentPath = resolve(import.meta.dirname, '../../../../../docs/api/gm-openapi.json');
const document = JSON.parse(readFileSync(documentPath, 'utf8')) as { paths: Record<string, Record<string, { operationId?: string }>> };

describe('parseCatalog (FR-065)', () => {
  it('keys every operation of the committed GM document by its route name', () => {
    const expected = Object.values(document.paths).flatMap((item) =>
      ['get', 'post', 'put', 'delete'].map((m) => item[m]?.operationId).filter((id): id is string => id !== undefined),
    );

    const catalog = parseCatalog(document);

    expect(expected.length).toBeGreaterThan(0);
    expect(catalog.operations.size).toBe(expected.length);
    expect([...catalog.operations.keys()].sort()).toEqual([...expected].sort());
  });

  it('reads method, template and the x-gm keys', () => {
    const catalog = parseCatalog(document);

    expect(catalog.operations.get('gm.player_receipts')).toMatchObject({
      method: 'GET',
      template: '/gm/v1/players/{playerId}/receipts',
      permission: 'player.read',
      kind: 'query',
      target: 'player',
      paged: true,
    });
    expect(catalog.operations.get('economy.adjust')).toMatchObject({ method: 'POST', kind: 'command', target: 'player', paged: false });
    expect(catalog.operations.get('gm.staff_grant')).toMatchObject({ method: 'PUT', kind: 'action', permission: 'staff.manage' });
  });

  it('defaults missing x-gm keys and skips operations without an operationId', () => {
    const catalog = parseCatalog({
      paths: {
        '/a': { get: { operationId: 'a.read', summary: 'Read a' }, post: { summary: 'no id' }, parameters: [] },
        '/b': { delete: { operationId: 'b.remove', 'x-gm-kind': 'weird', 'x-gm-target': 'moon', 'x-gm-paged': 'yes' } },
        '/c': 'not an object',
      },
    });

    expect([...catalog.operations.keys()]).toEqual(['a.read', 'b.remove']);
    expect(catalog.operations.get('a.read')).toEqual({
      routeName: 'a.read',
      method: 'GET',
      template: '/a',
      permission: '',
      kind: 'query',
      target: 'global',
      paged: false,
      summary: 'Read a',
    });
    expect(catalog.operations.get('b.remove')).toMatchObject({ method: 'DELETE', kind: 'query', target: 'global', paged: false });
  });

  it('gives an empty catalog for a value that is not a document', () => {
    expect(parseCatalog(null).operations.size).toBe(0);
    expect(parseCatalog({ paths: [] }).operations.size).toBe(0);
  });
});
