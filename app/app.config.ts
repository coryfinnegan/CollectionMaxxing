import type { ExpoConfig } from 'expo/config';

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
  ],
  experiments: {
    typedRoutes: true,
  },
};

export default config;
