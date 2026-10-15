import js from '@eslint/js';
import ts from 'typescript-eslint';
import svelte from 'eslint-plugin-svelte';
import prettier from 'eslint-config-prettier';
import globals from 'globals';

export default ts.config(
  {
    // Generated output, dependencies and editor artifacts — never lint these.
    ignores: ['dist/', 'build/', '.svelte-kit/', 'node_modules/', 'coverage/', '.vscode/']
  },
  js.configs.recommended,
  ...ts.configs.recommended,
  ...svelte.configs.recommended,
  // Turn off stylistic rules that conflict with Prettier.
  prettier,
  ...svelte.configs.prettier,
  {
    languageOptions: {
      globals: { ...globals.browser, ...globals.node }
    }
  },
  {
    files: ['**/*.svelte', '**/*.svelte.ts', '**/*.svelte.js'],
    languageOptions: {
      parserOptions: {
        // Use the TS parser for <script lang="ts"> blocks.
        parser: ts.parser,
        extraFileExtensions: ['.svelte']
      }
    }
  },
  {
    // Project-wide rule tuning. The codebase predates the linter, so the
    // higher-volume rules are kept at "warn" to give a green baseline; the
    // warnings are a visible backlog to burn down (then promote back to "error").
    rules: {
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/no-unused-vars': [
        'warn',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' }
      ],
      'svelte/require-each-key': 'warn',
      'svelte/no-navigation-without-resolve': 'warn',
      'svelte/prefer-svelte-reactivity': 'warn',
      // Markdown/changelog rendering uses {@html} deliberately; keep it visible
      // as a warning rather than blocking the build.
      'svelte/no-at-html-tags': 'warn',
      'svelte/no-unused-svelte-ignore': 'warn'
    }
  }
);
