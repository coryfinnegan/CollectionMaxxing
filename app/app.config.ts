import type { ExpoConfig } from 'expo/config';
import privacyManifest from './privacyManifest.json';

const config: ExpoConfig = {
  name: 'CollectionMaxxing',
  slug: 'collectionmaxxing',
  scheme: 'collectionmaxxing',
  version: '0.1.0',
  orientation: 'portrait',
  icon: './assets/images/icon.png',
  userInterfaceStyle: 'automatic',
  ios: {
    bundleIdentifier: 'io.github.coryfinnegan.collectionmaxxing',
    supportsTablet: false,
    // PrivacyInfo.xcprivacy (OPS-2a, #172): no tracking, no collected data, and the required-reason APIs the libraries use.
    privacyManifests: privacyManifest,
    infoPlist: {
      NSCameraUsageDescription: 'CollectionMaxxing uses the camera to read barcodes and recognise items. Pictures are processed on your phone.',
    },
  },
  web: {
    output: 'static',
    favicon: './assets/images/favicon.png',
  },
  plugins: [
    'expo-router',
    ['expo-system-ui', {}],
    'expo-secure-store',
    ['expo-local-authentication', { faceIDPermission: 'CollectionMaxxing uses Face ID to lock the app when you leave it.' }],
    ['expo-sensors', { motionPermission: 'CollectionMaxxing uses motion to take the photo when your phone is steady.' }],
    // Import from Photos (CAM-3d) uses the system picker only. No cameraPermission here: `false` would delete the
    // NSCameraUsageDescription above, which the scanner's own camera needs.
    [
      'expo-image-picker',
      {
        photosPermission: 'CollectionMaxxing reads the photos you pick to recognise the items in them. Pictures are processed on your phone.',
        microphonePermission: false,
      },
    ],
  ],
  experiments: {
    typedRoutes: true,
  },
};

export default config;
