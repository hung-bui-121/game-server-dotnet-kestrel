import { describe, expect, it } from 'vitest';
import { createFakeFetch } from '../test/fakeFetch';
import { loadConfig, validateConfig } from './loadConfig';

const BASE = 'http://localhost:5173/';
const LOCAL = { apiBaseUrl: 'http://localhost:5080', gameId: 'sample', environmentId: 'local' };
const BETA = { apiBaseUrl: 'https://gm-api.beta.example.com', gameId: 'zhpvp', environmentId: 'beta' };

async function load(body: unknown, status = 200) {
  const fake = createFakeFetch({ 'GET /config.json': { status, body } });
  const result = await loadConfig(fake.fetch, BASE);
  return { result, fake };
}

describe('loadConfig (FR-050, US6-1)', () => {
  it('loads the local and the Beta examples with one no-store request and no API call', async () => {
    for (const config of [LOCAL, BETA]) {
      const { result, fake } = await load(config);
      expect(result).toEqual({ ok: true, config });
      expect(fake.requests).toHaveLength(1);
      expect(fake.requests[0]).toMatchObject({ method: 'GET', url: 'http://localhost:5173/config.json', cache: 'no-store' });
    }
  });

  it('reads config.json next to the document, so a console under a sub-path works', async () => {
    const fake = createFakeFetch({ 'GET /gm/config.json': { body: LOCAL } });
    await expect(loadConfig(fake.fetch, 'https://cdn.example.com/gm/')).resolves.toMatchObject({ ok: true });
    expect(fake.requests[0]?.url).toBe('https://cdn.example.com/gm/config.json');
  });

  it('removes a trailing slash of apiBaseUrl and ignores unknown members', async () => {
    const { result } = await load({ ...BETA, apiBaseUrl: 'https://gm-api.beta.example.com/', future: { x: 1 } });
    expect(result).toEqual({ ok: true, config: BETA });
  });

  it('names the file when it is missing or not JSON', async () => {
    expect((await load(undefined, 404)).result).toEqual({
      ok: false,
      field: 'config.json',
      message: 'config.json could not be loaded (HTTP 404).',
    });

    const fake = createFakeFetch({
      'GET /config.json': () => ({ status: 200, body: undefined, headers: { 'Content-Type': 'text/html' } }),
    });
    expect(await loadConfig(fake.fetch, BASE)).toMatchObject({ ok: false, field: 'config.json', message: 'config.json is not valid JSON.' });
    expect((await load([LOCAL])).result).toMatchObject({ ok: false, field: 'config.json' });
  });

  it('names the file when the request fails', async () => {
    const fake = createFakeFetch({ 'GET /config.json': { networkError: true } });
    expect(await loadConfig(fake.fetch, BASE)).toMatchObject({ ok: false, field: 'config.json' });
  });

  it.each([
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: undefined }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 42 }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'localhost:5080' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: '/api' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'ftp://gm-api.example.com' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'javascript:alert(1)' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'https://gm-api.example.com?x=1' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'https://gm-api.example.com/#x' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'https://user:pw@gm-api.example.com' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'https://user@gm-api.example.com' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'HTTPS://gm-api.example.com' }],
    ['apiBaseUrl', { ...LOCAL, apiBaseUrl: 'https://gm api.example.com' }],
    ['gameId', { ...LOCAL, gameId: '' }],
    ['gameId', { ...LOCAL, gameId: 7 }],
    ['gameId', { ...LOCAL, gameId: 'g'.repeat(65) }],
    ['environmentId', { ...LOCAL, environmentId: undefined }],
    ['environmentId', { ...LOCAL, environmentId: 'e'.repeat(65) }],
  ])('refuses an invalid %s', (field, config) => {
    expect(validateConfig(config)).toMatchObject({ ok: false, field });
  });

  it('accepts 64-character ids', () => {
    expect(validateConfig({ ...LOCAL, gameId: 'g'.repeat(64), environmentId: 'e'.repeat(64) })).toMatchObject({ ok: true });
  });
});
