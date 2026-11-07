// End-to-end smoke of the GM console (slice 018 FR-087, research R11): Vitest in node + playwright-core with the system
// browser against a running run-local host. Sequential: one Vite server on port 5173 at a time.
import { defineConfig } from 'vitest/config';

const TEST_TIMEOUT_MS = 60_000;
/** First load: the dev target compiles on demand, the preview target builds first. */
const HOOK_TIMEOUT_MS = 120_000;

export default defineConfig({
  test: {
    name: 'e2e',
    root: import.meta.dirname,
    include: ['**/*.e2e.ts'],
    environment: 'node',
    fileParallelism: false,
    testTimeout: TEST_TIMEOUT_MS,
    hookTimeout: HOOK_TIMEOUT_MS,
  },
});
