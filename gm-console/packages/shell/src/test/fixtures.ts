import type { FakeRoute } from './fakeFetch';

export const CONFIG = { apiBaseUrl: 'http://localhost:5080', gameId: 'sample', environmentId: 'local' };

export const ALL_PERMISSIONS = ['gm.access', 'player.read', 'staff.read', 'ops.read'];

export function meBody(overrides: Record<string, unknown> = {}) {
  return {
    staffId: 'admin',
    displayName: 'Admin',
    gameId: 'sample',
    environmentId: 'local',
    permissions: ALL_PERMISSIONS,
    frameworkVersion: '0.7.0+abc',
    gameVersion: '1.0.0',
    ...overrides,
  };
}

/** The start routes of a console: config, methods, `/me`, CSRF, login, logout. */
export function sessionRoutes(overrides: Record<string, FakeRoute> = {}): Record<string, FakeRoute> {
  let token = 0;
  return {
    'GET /config.json': { body: CONFIG },
    'GET /gm/v1/auth/methods': { body: { local: true, sso: false } },
    'GET /gm/v1/me': { body: meBody() },
    'GET /gm/v1/auth/csrf': () => ({ body: { token: `csrf-${++token}` } }),
    'POST /gm/v1/auth/login': { body: meBody() },
    'POST /gm/v1/auth/logout': { status: 204 },
    ...overrides,
  };
}

/** A minimal GM document with the given operations (`routeName → [method, path, permission, extra]`). */
export function catalogDocument(operations: Record<string, [string, string, string, Record<string, unknown>?]>) {
  const paths: Record<string, Record<string, unknown>> = {};
  for (const [routeName, [method, path, permission, extra]] of Object.entries(operations)) {
    paths[path] = {
      ...paths[path],
      [method.toLowerCase()]: { operationId: routeName, 'x-gm-permission': permission, 'x-gm-kind': 'query', ...extra },
    };
  }

  return { openapi: '3.1.1', info: { title: 'GM', version: 'v1' }, paths };
}

/** The framework's read routes, as served by the sample host. */
export const READ_ROUTES: Record<string, [string, string, string, Record<string, unknown>?]> = {
  'gm.me': ['GET', '/gm/v1/me', 'gm.access'],
  'gm.player': ['GET', '/gm/v1/players/{playerId}', 'player.read', { 'x-gm-target': 'player' }],
  'gm.player_by_mapping': ['GET', '/gm/v1/players/by-mapping/{mappingKey}', 'player.read'],
  'gm.player_search': ['GET', '/gm/v1/players/search', 'player.read', { 'x-gm-paged': true }],
  'gm.player_receipts': ['GET', '/gm/v1/players/{playerId}/receipts', 'player.read', { 'x-gm-paged': true }],
  'gm.player_audit': ['GET', '/gm/v1/players/{playerId}/audit', 'player.read', { 'x-gm-paged': true }],
  'economy.ledger': ['GET', '/gm/v1/players/{playerId}/ledger', 'player.read', { 'x-gm-paged': true }],
  'gm.receipt': ['GET', '/gm/v1/receipts/{receiptId}', 'player.read'],
  'gm.staff_list': ['GET', '/gm/v1/staff', 'staff.read', { 'x-gm-paged': true }],
  'gm.staff': ['GET', '/gm/v1/staff/{staffId}', 'staff.read'],
  'gm.staff_audit': ['GET', '/gm/v1/staff/{staffId}/audit', 'staff.read', { 'x-gm-paged': true }],
  'gm.health': ['GET', '/gm/v1/health', 'ops.read'],
  'gm.ops_version': ['GET', '/gm/v1/ops/version', 'ops.read'],
  'gm.ops_gate': ['GET', '/gm/v1/ops/gate', 'ops.read'],
};
