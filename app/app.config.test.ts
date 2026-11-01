import type { ExpoConfig } from 'expo/config';
import config from './app.config';

const CI_SIGNING = ['IOS_SIGNING_TEAM_ID', 'IOS_SIGNING_PROFILE', 'IOS_BUILD_NUMBER'] as const;

/** app.config.ts as ios-build.yml sees it, with these environment variables set while it loads. */
function configWith(env: Partial<Record<(typeof CI_SIGNING)[number], string>>): ExpoConfig {
  const saved = Object.fromEntries(CI_SIGNING.map((k) => [k, process.env[k]]));
  try {
    for (const k of CI_SIGNING) delete process.env[k];
    Object.assign(process.env, env);
    let loaded: ExpoConfig | undefined;
    jest.isolateModules(() => {
      loaded = (require('./app.config') as typeof import('./app.config')).default;
    });
    return loaded!;
  } finally {
    for (const k of CI_SIGNING) {
      if (saved[k] === undefined) delete process.env[k];
      else process.env[k] = saved[k];
    }
  }
}

const signingPlugin = (c: ExpoConfig) => c.plugins?.find((p) => Array.isArray(p) && p[0] === './plugins/withReleaseSigning');

/** The iOS options app.config.ts hands to expo-build-properties. */
const buildProperties = (c: ExpoConfig) => {
  const plugin = c.plugins?.find((p) => Array.isArray(p) && p[0] === 'expo-build-properties') as [string, { ios?: Record<string, unknown> }] | undefined;
  return plugin?.[1].ios;
};

describe('app config', () => {
  // iOS 27 aborts at launch an app that skips the UIScene lifecycle; SDK 57's template needs this opt-in (no-op on SDK 58+).
  it('adopts the UIScene lifecycle so the app launches on iOS 27', () => {
    expect(buildProperties(config)).toMatchObject({ enableSceneSupport: true });
  });

  // The iOS archive compiles third-party C++ (RNScreens, Reanimated, SVG, VisionCamera) for most of its time; ccache lets
  // the next build reuse it (ios-build.yml keeps the cache).
  it('compiles native code through ccache so the iOS archive can reuse it', () => {
    expect(buildProperties(config)).toMatchObject({ ccacheEnabled: true });
  });

  // Without this answer every TestFlight build waits in App Store Connect for the export compliance question (ADR-024).
  it('declares it uses only exempt encryption (HTTPS), so TestFlight builds go straight to testers', () => {
    expect(config.ios?.infoPlist?.ITSAppUsesNonExemptEncryption).toBe(false);
  });

  it('keeps the bundle identifier the App Store Connect app and the provisioning profile are registered for', () => {
    expect(config.ios?.bundleIdentifier).toBe('io.github.coryfinnegan.collectionmaxxing');
  });

  it('registers the Game Boy alternate icon and the Android adaptive set (#364)', () => {
    const plugin = config.plugins?.find((p) => Array.isArray(p) && p[0] === './plugins/withAlternateAppIcons');
    expect(plugin).toEqual(['./plugins/withAlternateAppIcons', { icons: [{ name: 'gameboy', source2x: './assets/icons/icon-gameboy@2x.png', source3x: './assets/icons/icon-gameboy@3x.png' }] }]);
    expect(config.android?.adaptiveIcon).toEqual({
      foregroundImage: './assets/images/android-icon-foreground.png',
      backgroundImage: './assets/images/android-icon-background.png',
      monochromeImage: './assets/images/android-icon-monochrome.png',
    });
  });
});

describe('app config in the TestFlight build', () => {
  it('passes the team and the App Store profile from CI to the release signing plugin', () => {
    const c = configWith({ IOS_SIGNING_TEAM_ID: 'ABCDE12345', IOS_SIGNING_PROFILE: 'CollectionMaxxing App Store' });
    expect(signingPlugin(c)).toEqual(['./plugins/withReleaseSigning', { teamId: 'ABCDE12345', profile: 'CollectionMaxxing App Store' }]);
  });

  it('asks for no signing outside CI, so local prebuilds and the web export are unchanged', () => {
    const c = configWith({});
    expect(signingPlugin(c)).toEqual(['./plugins/withReleaseSigning', { teamId: undefined, profile: undefined }]);
  });

  it('takes the build number from CI, since every upload of a version needs a higher one', () => {
    expect(configWith({ IOS_BUILD_NUMBER: '57.1' }).ios?.buildNumber).toBe('57.1');
    expect(configWith({}).ios?.buildNumber).toBe('1');
  });

  it('refuses a build number App Store Connect would reject', () => {
    expect(() => configWith({ IOS_BUILD_NUMBER: 'v57' })).toThrow('IOS_BUILD_NUMBER');
    expect(() => configWith({ IOS_BUILD_NUMBER: '1.2.3.4' })).toThrow('IOS_BUILD_NUMBER');
  });
});
