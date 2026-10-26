// The display appearance choice (#363): Light, Dark, or follow the System. Kept on
// this device; RootFrame applies it at startup and the Settings screen applies it
// live. The effective scheme drives useColors, the navigator theme, and the
// castle's day and torchlit-night art together, so they can never disagree.
import { useColorScheme } from 'react-native';
import { UnistylesRuntime } from 'react-native-unistyles';
import { create } from 'zustand';

export type AppearanceChoice = 'light' | 'dark' | 'system';
export type EffectiveScheme = 'light' | 'dark';

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openAppearanceStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'display-settings' });
  } catch {
    return memoryStore();
  }
}

const KEY = 'appearance';
let storage: StringStore | undefined;

export const getAppearanceStorage = (): StringStore => (storage ??= openAppearanceStorage());
export const setAppearanceStorage = (next: StringStore | undefined) => {
  storage = next;
};

const readChoice = (): AppearanceChoice => {
  const stored = getAppearanceStorage().getString(KEY);
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
};

type AppearanceState = { choice: AppearanceChoice; set(choice: AppearanceChoice): void; reload(): void };

/** Follows the system until the person picks Light or Dark in Settings; kept on this device. */
export const useAppearanceChoice = create<AppearanceState>((set) => ({
  choice: 'system',
  set: (choice) => {
    getAppearanceStorage().set(KEY, choice);
    set({ choice });
  },
  reload: () => set({ choice: readChoice() }),
}));

/** The scheme the app paints: the choice, or the system scheme while it is still loading. */
export function useEffectiveScheme(): EffectiveScheme {
  const system = useColorScheme();
  const choice = useAppearanceChoice((s) => s.choice);
  if (choice !== 'system') return choice;
  return system === 'dark' ? 'dark' : 'light';
}

type ThemeRuntime = { setTheme(themeName: string): void; setAdaptiveThemes(isEnabled: boolean): void };

/** Points the styled layer at the choice: System follows the OS, Light and Dark pin it. */
export function applyAppearance(choice: AppearanceChoice, runtime: ThemeRuntime = UnistylesRuntime as unknown as ThemeRuntime): void {
  if (choice === 'system') {
    runtime.setAdaptiveThemes(true);
    return;
  }
  runtime.setAdaptiveThemes(false);
  runtime.setTheme(choice);
}
