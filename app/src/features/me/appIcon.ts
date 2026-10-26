// The home screen icon choice (Settings → Appearance, #364): the default dragon or the
// Game Boy alternate. The iPhone is the source of truth; the stored choice corrects itself
// from it on every refresh.
import { create } from 'zustand';
import { getAppIcon, getSettingsStorage } from './services';

export type AppIconChoice = 'default' | 'gameboy';

/** The CFBundleAlternateIcons key the config plugin registers; null means the default icon. */
export const GAMEBOY_ICON_NAME = 'gameboy';

const KEY = 'app-icon-choice';

type AppIconSetting = {
  choice: AppIconChoice;
  supported: boolean;
  ready: boolean;
  problem: string | null;
  refresh(): Promise<void>;
  set(choice: AppIconChoice): Promise<void>;
};

export const useAppIconSetting = create<AppIconSetting>((set, get) => ({
  choice: 'default',
  supported: false,
  ready: false,
  problem: null,
  refresh: async () => {
    let supported = false;
    try {
      supported = await getAppIcon().supportsAlternateIcons();
    } catch {
      supported = false;
    }
    if (!supported) {
      set({ supported: false, ready: true });
      return;
    }
    let name: string | null = null;
    try {
      name = await getAppIcon().currentIconName();
    } catch {
      name = null;
    }
    const choice: AppIconChoice = name === GAMEBOY_ICON_NAME ? 'gameboy' : 'default';
    getSettingsStorage().set(KEY, choice);
    set({ choice, supported: true, ready: true });
  },
  set: async (choice) => {
    if (!get().supported || choice === get().choice) return;
    try {
      await getAppIcon().setIconName(choice === 'gameboy' ? GAMEBOY_ICON_NAME : null);
      getSettingsStorage().set(KEY, choice);
      set({ choice, problem: null });
    } catch {
      set({ problem: "Couldn't switch the icon. Try again." });
    }
  },
}));
