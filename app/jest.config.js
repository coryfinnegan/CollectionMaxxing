const preset = require('jest-expo/jest-preset');

// msw's helper `rettime` ships ESM only (like until-async), so Jest has to run it through Babel too.
const esmOnlyDependencies = ['rettime', 'until-async', '@open-draft', '@mswjs', 'strict-event-emitter', 'outvariant', 'headers-polyfill'];
const transformIgnorePatterns = preset.transformIgnorePatterns.map((pattern) =>
  pattern.includes('(?!') ? pattern.replace('(?!', `(?!(?:${esmOnlyDependencies.join('|')})/|`) : pattern,
);

/** @type {import('jest').Config} */
module.exports = {
  preset: 'jest-expo',
  // CI restores this folder between runs, so the first test files don't pay for a cold Babel transform (#87).
  ...(process.env.JEST_CACHE_DIR ? { cacheDirectory: process.env.JEST_CACHE_DIR } : {}),
  // Unistyles 3 (#305): the documented mocks first, then the app config, so themed
  // StyleSheets resolve (to the first theme) in tests. The preset's own setup
  // files stay first — overriding setupFiles would drop the RN/Expo setup.
  setupFiles: [...(preset.setupFiles ?? []), 'react-native-unistyles/mocks', '<rootDir>/src/theme/unistyles.ts'],
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
    // expo-router's testing setup mocks reanimated with the shipped mock, which cannot load here
    // (it pulls the native worklets); point it at our stand-in instead (#302).
    '^react-native-reanimated/mock$': '<rootDir>/__mocks__/reanimated-impl.js',
    // Mirrors the tsconfig paths: the Lottie spots live next to src, not in it (#304).
    '^@/assets/(.*)$': '<rootDir>/assets/$1',
    '^@/(.*)$': '<rootDir>/src/$1',
  },
};
