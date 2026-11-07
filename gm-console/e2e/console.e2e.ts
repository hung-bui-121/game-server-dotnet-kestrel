import type { Browser } from 'playwright-core';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { checkApi, CONSOLE_URL, launchBrowser, openRecorder, startTarget, TARGETS, targetSkipReason } from './harness';

/** T054: the harness itself — each target serves the console, which reaches the login page of the running host. */
describe.each(TARGETS)('harness, %s target', (target) => {
  const skip = targetSkipReason(target);
  if (skip) {
    it.skip(skip, () => undefined);
    return;
  }

  let stop: (() => Promise<void>) | undefined;
  let browser: Browser | undefined;

  beforeAll(async () => {
    await checkApi();
    stop = await startTarget(target);
    browser = await launchBrowser();
  });

  afterAll(async () => {
    await browser?.close();
    await stop?.();
  });

  it('opens the console on the login page', async () => {
    const context = await browser!.newContext();
    const { page } = await openRecorder(context);

    await page.goto(CONSOLE_URL);

    await page.getByRole('form', { name: 'Local sign-in' }).waitFor({ timeout: 30_000 });
    expect(await page.title()).toBe('GM console');
    await context.close();
  });
});
