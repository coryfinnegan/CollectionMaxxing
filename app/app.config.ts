import type { ExpoConfig } from 'expo/config';
import privacyManifest from './privacyManifest.json';

// The TestFlight build (ios-build.yml, ADR-024) passes these at prebuild. Each upload of a version needs a higher build
// number; App Store Connect takes one to three dot-separated integers.
const buildNumber = process.env.IOS_BUILD_NUMBER || '1';
if (!/^\d+(\.\d+){0,2}$/.test(buildNumber)) {
  throw new Error(`IOS_BUILD_NUMBER should be one to three dot-separated integers, got "${buildNumber}".`);
}

// Registered in the Apple Developer account; the App Store Connect app and the provisioning profile are tied to it.
const bundleIdentifier = 'io.github.coryfinnegan.collectionmaxxing';

const config: ExpoConfig = {
  name: 'CollectionMaxxing',
  slug: 'collectionmaxxing',
  scheme: 'collectionmaxxing',
  version: '1.2.1',
  orientation: 'portrait',
  icon: './assets/images/icon.png',
  userInterfaceStyle: 'automatic',
  ios: {
    bundleIdentifier,
    buildNumber,
    supportsTablet: false,
    // PrivacyInfo.xcprivacy (OPS-2a, #172): no tracking, no collected data, and the required-reason APIs the libraries use.
    privacyManifests: privacyManifest,
    infoPlist: {
      NSCameraUsageDescription: 'CollectionMaxxing uses the camera to read barcodes and recognize items. Pictures are processed on your phone.',
      // Only HTTPS, which is exempt: without this every TestFlight build stops at the export compliance question.
      ITSAppUsesNonExemptEncryption: false,
    },
  },
  android: {
    adaptiveIcon: {
      foregroundImage: './assets/images/android-icon-foreground.png',
      backgroundImage: './assets/images/android-icon-background.png',
      monochromeImage: './assets/images/android-icon-monochrome.png',
    },
  },
  web: {
    output: 'static',
    favicon: './assets/images/favicon.png',
  },
  plugins: [
    'expo-router',
    // iOS 27 kills an app that doesn't use the UIScene lifecycle at launch (UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption).
    // SDK 57 still generates the old AppDelegate-only template, so opt in; this is a no-op from SDK 58 on.
    ['expo-build-properties', { ios: { enableSceneSupport: true } }],
    ['expo-system-ui', {}],
    'expo-secure-store',
    ['expo-local-authentication', { faceIDPermission: 'CollectionMaxxing uses Face ID to lock the app when you leave it.' }],
    ['expo-sensors', { motionPermission: 'CollectionMaxxing uses motion to take the photo when your phone is steady.' }],
    // Import from Photos (CAM-3d) uses the system picker only. No cameraPermission here: `false` would delete the
    // NSCameraUsageDescription above, which the scanner's own camera needs.
    [
      'expo-image-picker',
      {
        photosPermission: 'CollectionMaxxing reads the photos you pick to recognize the items in them. Pictures are processed on your phone.',
        microphonePermission: false,
      },
    ],
    // Auth0 sign-in (docs/14, ADR-025). The callback is <scheme>://<domain>/ios/<bundle id>/callback, which the iPhone
    // app in deploy/auth0 allows; the scheme is the SDK's default, spelled out because the plugin can't infer it for
    // Android. ios-build.yml passes the real domain; the placeholder keeps CI and local builds working.
    [
      'react-native-auth0',
      { domain: process.env.EXPO_PUBLIC_AUTH0_DOMAIN || 'auth0-domain.invalid', customScheme: `${bundleIdentifier}.auth0` },
    ],
    // Manual signing of the Release build with the App Store profile, only when CI passes both (ADR-024).
    ['./plugins/withReleaseSigning', { teamId: process.env.IOS_SIGNING_TEAM_ID, profile: process.env.IOS_SIGNING_PROFILE }],
    // Alternate home screen icons (#364): the Game Boy dragon, switched in Settings Appearance.
    ['./plugins/withAlternateAppIcons', { icons: [{ name: 'gameboy', source2x: './assets/icons/icon-gameboy@2x.png', source3x: './assets/icons/icon-gameboy@3x.png' }] }],
  ],
  experiments: {
    typedRoutes: true,
  },
};

export default config;
