import js from '@eslint/js';
import globals from 'globals';
import tsPlugin from '@typescript-eslint/eslint-plugin';
import tsParser from '@typescript-eslint/parser';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import jsxA11y from 'eslint-plugin-jsx-a11y';

export default [
  {
    // Mirrors what a .eslintignore would hold; flat config has no separate ignore file.
    ignores: [
      'dist/**',
      'node_modules/**',
      'coverage/**',
      'playwright-report/**',
      'test-results/**',
      '**/*.min.js',
    ],
  },
  js.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: { ...globals.browser, ...globals.es2022 },
      parser: tsParser,
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: {
      '@typescript-eslint': tsPlugin,
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      'jsx-a11y': jsxA11y,
    },
    rules: {
      ...tsPlugin.configs.recommended.rules,
      ...reactHooks.configs.recommended.rules,
      // Clarification Q4 makes WCAG 2.1 AA a delivery requirement, so a11y findings fail lint.
      ...jsxA11y.configs.recommended.rules,
      // The core no-undef rule cannot see TypeScript's type-only globals, so it reports `React`,
      // `RequestInit` and similar DOM lib types as undefined. TypeScript already fails the build
      // on a genuinely undefined identifier and does it far more accurately, so leaving this on
      // would only train people to ignore lint output. This is typescript-eslint's own
      // recommendation for TypeScript sources.
      'no-undef': 'off',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      // Principle IV: untrusted content is never trusted. Injecting raw HTML in the SPA would
      // put uploaded content on the application origin, which is exactly what the isolated
      // preview origin exists to prevent.
      'react/no-danger': 'off',
      'no-restricted-properties': [
        'error',
        {
          object: 'document',
          property: 'write',
          message: 'document.write is forbidden.',
        },
      ],
      'no-restricted-syntax': [
        'error',
        {
          selector: 'JSXAttribute[name.name="dangerouslySetInnerHTML"]',
          message:
            'dangerouslySetInnerHTML is forbidden. Uploaded content renders only in the isolated preview origin (Principle IV).',
        },
        {
          selector: 'MemberExpression[property.name="innerHTML"]',
          message:
            'innerHTML is forbidden. Uploaded content renders only in the isolated preview origin (Principle IV).',
        },
      ],
    },
  },
  {
    files: ['tests/**/*.{ts,tsx}'],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
  },
  {
    // Build and tooling code runs in Node, not in a browser.
    files: [
      'vite.config.ts',
      'vitest.config.ts',
      'playwright.config.ts',
      'eslint.config.js',
      'scripts/**/*.mjs',
    ],
    languageOptions: { globals: { ...globals.node } },
    rules: {
      // Tooling talks to the operator through stdout; that is its interface, not a stray debug
      // statement left behind.
      'no-console': 'off',
    },
  },
];
