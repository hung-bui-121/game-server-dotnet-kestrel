// ESLint flat config of the GM console workspace (slice 018, FR-048: design §12.2, §12.6, SEC-12; FR-074 boundary).
// The security and boundary rules live in eslint.rules.js (also used by eslint.rules.test.ts).
import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import { boundaryConfigs } from './eslint.rules.js';

export default defineConfig(
  {
    ignores: ['**/node_modules/**', '**/dist/**', 'e2e/.artifacts/**', 'packages/shell/src/generated/**'],
  },
  js.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    extends: [tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      parserOptions: {
        project: [
          './packages/shell/tsconfig.test.json',
          './app/tsconfig.json',
          './tsconfig.tools.json',
        ],
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      '@typescript-eslint/no-implied-eval': 'error',
      '@typescript-eslint/consistent-type-imports': 'error',
    },
  },
  {
    files: ['**/*.{js,mjs,cjs}'],
    languageOptions: { globals: globals.node },
  },
  ...boundaryConfigs(),
);
