const expoConfig = require('eslint-config-expo/flat');

module.exports = [
  { ignores: ['dist/*', 'src/api/generated/*', 'public/*', '.expo/*'] },
  ...expoConfig,
  {
    rules: {
      'no-console': ['error', { allow: ['warn', 'error'] }],
    },
  },
];
