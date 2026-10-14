const preset = require('jest-expo/jest-preset');

// msw's helper `rettime` ships ESM only (like until-async), so Jest has to run it through Babel too.
const esmOnlyDependencies = ['rettime', 'until-async', '@open-draft', '@mswjs', 'strict-event-emitter', 'outvariant', 'headers-polyfill'];
const transformIgnorePatterns = preset.transformIgnorePatterns.map((pattern) =>
  pattern.includes('(?!') ? pattern.replace('(?!', `(?!(?:${esmOnlyDependencies.join('|')})/|`) : pattern,
);

/** @type {import('jest').Config} */
module.exports = {
  preset: 'jest-expo',
  setupFilesAfterEnv: ['<rootDir>/jest.setup.ts'],
  testPathIgnorePatterns: ['/node_modules/', '/dist/', '/playwright/'],
  transformIgnorePatterns,
  transform: {
    ...preset.transform,
    '\\.mjs$': preset.transform['\\.[jt]sx?$'],
  },
  moduleNameMapper: {
    // msw's package exports null out the react-native condition that jest-expo resolves with
    '^msw/node$': '<rootDir>/node_modules/msw/lib/node/index.js',
    '^@/(.*)$': '<rootDir>/src/$1',
  },
};
