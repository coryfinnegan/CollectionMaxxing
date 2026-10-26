// The dragon appearances setting (#363): Full shows every placement with motion,
// Minimal shows only the reactive state poses (empty, loading, error, milestone)
// held still, and Off shows no dragon anywhere. Kept on this device; the Settings
// screen applies it live. Reduce Motion still holds every pose still on Full.
import { create } from 'zustand';

export type DragonAppearance = 'full' | 'minimal' | 'off';

/** Decorative placements (the header perch) show on Full only; state placements also show on Minimal. */
export type MascotPlacement = 'decorative' | 'state';

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openMascotStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'mascot-settings' });
  } catch {
    return memoryStore();
  }
}

const KEY = 'dragon-appearance';
let storage: StringStore | undefined;

export const getMascotStorage = (): StringStore => (storage ??= openMascotStorage());
export const setMascotStorage = (next: StringStore | undefined) => {
  storage = next;
};

type DragonState = { appearance: DragonAppearance; set(appearance: DragonAppearance): void; reload(): void };

/** Full until the person picks Minimal or Off in Settings; kept on this device. */
export const useDragonAppearance = create<DragonState>((set) => ({
  appearance: 'full',
  set: (appearance) => {
    getMascotStorage().set(KEY, appearance);
    set({ appearance });
  },
  reload: () => {
    const stored = getMascotStorage().getString(KEY);
    set({ appearance: stored === 'minimal' || stored === 'off' ? stored : 'full' });
  },
}));

/** Whether a placement shows under an appearance: Off hides everything, Minimal keeps state poses only. */
export function placementVisible(appearance: DragonAppearance, placement: MascotPlacement): boolean {
  if (appearance === 'off') return false;
  if (appearance === 'minimal') return placement === 'state';
  return true;
}
