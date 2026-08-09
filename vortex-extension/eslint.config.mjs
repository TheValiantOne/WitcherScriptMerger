// Flat ESLint config (ESLint 9+). Basic, sane TypeScript linting - not the type-checked
// preset (`recommendedTypeChecked`), to keep this fast and dependency-light for a v1
// scaffold; a later unit can upgrade to type-aware rules if that becomes worthwhile.
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: ['dist/**', 'node_modules/**'],
  },
  ...tseslint.configs.recommended,
  {
    rules: {
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
    },
  },
);
