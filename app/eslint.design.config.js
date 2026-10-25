// The design-ratchet config (#307): cm-design rules as errors. Used only by
// scripts/design-gate.mjs against the baseline; `npm run lint` is unchanged.
const tsParser = require('@typescript-eslint/parser');
const cmDesign = require('./eslint-rules/cm-design');

module.exports = [
  { ignores: ['dist/*', 'src/api/generated/*', 'public/*', '.expo/*', 'node_modules/*'] },
  {
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/**/*.test.{ts,tsx}'],
    languageOptions: {
      parser: tsParser,
      ecmaVersion: 'latest',
      sourceType: 'module',
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: { 'cm-design': cmDesign },
    rules: {
      'cm-design/no-raw-colors': 'error',
      'cm-design/no-inline-styles': 'error',
      'cm-design/no-raw-text': 'error',
      'cm-design/no-doubled-title': 'error',
      'cm-design/no-gradients': 'error',
    },
  },
];
