// The swappable pieces of the update notice, replaced by fakes in tests: where the notice remembers things, the clock,
// which version is installed and how the download is opened.
import Constants from 'expo-constants';
import { Linking } from 'react-native';
import { getSettingsStorage } from '@/features/me/services';
import type { KeyValueStore } from '@/features/scan/tray/trayStore';

export type UpdateEnvironment = {
  store: KeyValueStore;
  now: () => number;
  /** The version of the installed app, from app.config.ts. */
  appVersion: () => string | null | undefined;
  /** Opens the download in the system browser (Safari). */
  open: (url: string) => Promise<unknown>;
};

let overrides: Partial<UpdateEnvironment> = {};

export const setUpdateEnvironment = (next: Partial<UpdateEnvironment>) => {
  overrides = { ...overrides, ...next };
};
export const resetUpdateEnvironment = () => {
  overrides = {};
};

export const updateEnvironment = (): UpdateEnvironment => ({
  store: overrides.store ?? getSettingsStorage(),
  now: overrides.now ?? Date.now,
  appVersion: overrides.appVersion ?? (() => Constants.expoConfig?.version),
  open: overrides.open ?? ((url) => Linking.openURL(url)),
});
