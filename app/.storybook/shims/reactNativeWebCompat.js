// Storybook-only react-native/web compatibility layer. Everything re-exports
// react-native-web, plus the RN-only TurboModule surface that native libraries
// (e.g. reanimated) import but can never successfully use on web: Metro
// resolves those to the real Flow-typed RN package, where lookups simply miss.
// get() returns null exactly as it does on web under Metro; getEnforcing()
// fails loudly so a silent behavior gap is impossible.
export * from 'react-native-web';

export const TurboModuleRegistry = {
  get: () => null,
  getEnforcing: (name) => {
    throw new Error(`[Storybook] TurboModule '${name}' is not available on web`);
  },
};
