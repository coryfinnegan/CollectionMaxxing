// iOS privacy manifest (PrivacyInfo.xcprivacy) as a typed object; app.config.ts loads the same JSON.
// No tracking, no tracking domains, no collected data types (docs/08 "Privacy").
import manifest from './privacyManifest.json';

/** Apple's published reason codes per required-reason API category. */
export const APPLE_REASON_CODES: Record<string, readonly string[]> = {
  NSPrivacyAccessedAPICategoryFileTimestamp: ['DDA9.1', 'C617.1', '3B52.1', '0A2A.1'],
  NSPrivacyAccessedAPICategoryDiskSpace: ['85F4.1', 'E174.1', '7D9E.1', 'B728.1'],
  NSPrivacyAccessedAPICategorySystemBootTime: ['35F9.1', '8FFB.1', '3D61.1'],
  NSPrivacyAccessedAPICategoryUserDefaults: ['CA92.1', '1C8F.1', 'C56D.1', 'AC6B.1'],
};

export type PrivacyManifest = {
  NSPrivacyTracking: boolean;
  NSPrivacyTrackingDomains: string[];
  NSPrivacyCollectedDataTypes: never[];
  NSPrivacyAccessedAPITypes: { NSPrivacyAccessedAPIType: string; NSPrivacyAccessedAPITypeReasons: string[] }[];
};

// The data lives in privacyManifest.json, because app.config.ts can only load JSON (Expo's config loader can't import
// another TypeScript file). Why each category is there:
//  - FileTimestamp: react-native core and MMKV read file times; expo-file-system reads them for the app's own files.
//  - DiskSpace: expo-file-system and MMKV check free space before writing the app's own files.
//  - SystemBootTime: react-native core measures elapsed time with the boot clock.
//  - UserDefaults: react-native core and expo-secure-store keep the app's own settings in user defaults.
export const privacyManifest: PrivacyManifest = manifest as PrivacyManifest;
