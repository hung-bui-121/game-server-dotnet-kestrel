// Unit and component tests of the workspace (slice 018): the shell in jsdom, the deploy consistency checks in node.
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/** The build stamp seen by tests (the app build defines the real one, FR-056). */
const TEST_BUILT_FOR = JSON.stringify({ frameworkVersion: '0.7.0' });

export default defineConfig({
  test: {
    passWithNoTests: true,
    projects: [
      {
        plugins: [react()],
        define: { __GM_BUILT_FOR__: TEST_BUILT_FOR },
        test: {
          name: 'shell',
          root: './packages/shell',
          environment: 'jsdom',
          include: ['src/**/*.test.{ts,tsx}'],
          setupFiles: ['./src/test/setup.ts'],
        },
      },
      {
        test: {
          name: 'tooling',
          root: '.',
          environment: 'node',
          include: ['eslint.rules.test.ts'],
        },
      },
      {
        test: {
          name: 'deploy',
          root: './deploy',
          environment: 'node',
          include: ['**/*.test.ts'],
        },
      },
    ],
  },
});
