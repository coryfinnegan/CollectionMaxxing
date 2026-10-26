// The castle render mode (#361): Full shows the textures, Flat paints the same
// palette solid, Accessible drops the textures and motion for maximum legibility.
// The Theme setting (Full/Quiet) picks between the first two; the system Increase
// Contrast and Reduce Motion signals force Accessible, which wins on conflict.
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Platform } from 'react-native';
import { create } from 'zustand';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';

export type CastleMode = 'full' | 'flat' | 'accessible';
export type CastleThemeSetting = 'full' | 'quiet';

export function resolveCastleMode(
  setting: CastleThemeSetting,
  signals: { increaseContrast: boolean; reduceMotion: boolean },
): CastleMode {
  if (signals.increaseContrast || signals.reduceMotion) return 'accessible';
  return setting === 'quiet' ? 'flat' : 'full';
}

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openCastleStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'castle-settings' });
  } catch {
    return memoryStore();
  }
}

const KEY = 'castle-theme';
let storage: StringStore | undefined;

export const getCastleStorage = (): StringStore => (storage ??= openCastleStorage());
export const setCastleStorage = (next: StringStore | undefined) => {
  storage = next;
};

type CastleThemeState = { setting: CastleThemeSetting; set(setting: CastleThemeSetting): void; reload(): void };

/** Full until the person picks Quiet in Settings (wired in Phase 1); kept on this device. */
export const useCastleThemeSetting = create<CastleThemeState>((set) => ({
  setting: 'full',
  set: (setting) => {
    getCastleStorage().set(KEY, setting);
    set({ setting });
  },
  reload: () => set({ setting: getCastleStorage().getString(KEY) === 'quiet' ? 'quiet' : 'full' }),
}));

type ContrastQuery = {
  matches: boolean;
  addEventListener(type: 'change', fn: (e: { matches: boolean }) => void): void;
  removeEventListener(type: 'change', fn: (e: { matches: boolean }) => void): void;
};

function webContrastQuery(): { query(): ContrastQuery | undefined } {
  const globals = globalThis as { window?: { matchMedia?: (query: string) => ContrastQuery } };
  return { query: () => globals.window?.matchMedia?.('(prefers-contrast: more)') };
}

/** True when the system asks for stronger contrast: darker colors (iOS), high text contrast (Android), prefers-contrast (web). */
export function useIncreaseContrast(): boolean {
  const [contrast, setContrast] = useState(() => (Platform.OS === 'web' ? (webContrastQuery().query()?.matches ?? false) : false));
  useEffect(() => {
    if (Platform.OS === 'web') {
      const query = webContrastQuery().query();
      if (!query) return;
      const onChange = (e: { matches: boolean }) => setContrast(e.matches);
      query.addEventListener('change', onChange);
      return () => query.removeEventListener('change', onChange);
    }
    let alive = true;
    const read = Platform.OS === 'ios' ? AccessibilityInfo.isDarkerSystemColorsEnabled : AccessibilityInfo.isHighTextContrastEnabled;
    void read()
      .then((on) => alive && setContrast(on))
      .catch(() => {});
    const event = Platform.OS === 'ios' ? 'darkerSystemColorsChanged' : 'highTextContrastChanged';
    const sub = AccessibilityInfo.addEventListener(event, setContrast);
    return () => {
      alive = false;
      sub.remove();
    };
  }, []);
  return contrast;
}

/** The mode the castle primitives render in: the setting, overridden by the system signals. */
export function useCastleMode(): CastleMode {
  const setting = useCastleThemeSetting((s) => s.setting);
  const increaseContrast = useIncreaseContrast();
  const reduceMotion = useReduceMotion();
  return resolveCastleMode(setting, { increaseContrast, reduceMotion });
}
