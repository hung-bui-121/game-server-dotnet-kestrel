import { describe, expect, it, vi } from 'vitest';
import { createCsrfStore } from './csrf';

describe('createCsrfStore (FR-052)', () => {
  it('fetches once, shares concurrent requests and keeps the token in memory', async () => {
    let n = 0;
    const fetchToken = vi.fn(() => Promise.resolve(`t${++n}`));
    const store = createCsrfStore(fetchToken);

    const [a, b] = await Promise.all([store.get(), store.get()]);
    expect([a, b, await store.get()]).toEqual(['t1', 't1', 't1']);
    expect(fetchToken).toHaveBeenCalledTimes(1);

    expect(await store.refresh()).toBe('t2');
    expect(await store.get()).toBe('t2');

    store.clear();
    expect(await store.get()).toBe('t3');
    expect(fetchToken).toHaveBeenCalledTimes(3);
  });

  it('ignores a token that arrives after the store was cleared', async () => {
    let resolve: (value: string) => void = () => undefined;
    const store = createCsrfStore(
      vi
        .fn<() => Promise<string>>()
        .mockImplementationOnce(() => new Promise((r) => (resolve = r)))
        .mockResolvedValue('fresh'),
    );

    const stale = store.get();
    store.clear();
    resolve('stale');
    await stale;

    expect(await store.get()).toBe('fresh');
  });
});

describe('createCsrfStore refresh (review T068 m2)', () => {
  it('shares one in-flight refresh between concurrent callers', async () => {
    let n = 0;
    const fetchToken = vi.fn(() => Promise.resolve(`t${++n}`));
    const store = createCsrfStore(fetchToken);

    const tokens = await Promise.all([store.refresh(), store.refresh(), store.refresh()]);

    expect(tokens).toEqual(['t1', 't1', 't1']);
    expect(fetchToken).toHaveBeenCalledTimes(1);
    expect(await store.refresh()).toBe('t2');
  });

  it('after clear() never reuses a refresh that started before it', async () => {
    let resolveOld: (value: string) => void = () => undefined;
    const fetchToken = vi
      .fn<() => Promise<string>>()
      .mockImplementationOnce(() => new Promise((r) => (resolveOld = r)))
      .mockResolvedValue('after-clear');
    const store = createCsrfStore(fetchToken);

    const old = store.refresh();
    store.clear();
    const fresh = store.refresh();
    resolveOld('before-clear');

    expect(await old).toBe('before-clear');
    expect(await fresh).toBe('after-clear');
    expect(await store.get()).toBe('after-clear');
    expect(fetchToken).toHaveBeenCalledTimes(2);
  });
});
