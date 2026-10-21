import config from './app.config';

describe('app config', () => {
  // iOS 27 aborts at launch an app that skips the UIScene lifecycle; SDK 57's template needs this opt-in (no-op on SDK 58+).
  it('adopts the UIScene lifecycle so the app launches on iOS 27', () => {
    const plugin = config.plugins?.find((p) => Array.isArray(p) && p[0] === 'expo-build-properties');
    expect(plugin).toEqual(['expo-build-properties', { ios: { enableSceneSupport: true } }]);
  });
});
