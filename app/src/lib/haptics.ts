// Scan feedback in three patterns (docs/06): owned = two short taps, new = one tap, not found = one long tap.
// The patterns talk to a small driver so tests can use a fake; the default driver uses expo-haptics and
// does nothing on the web.
import { Platform } from 'react-native';

export type HapticDriver = {
  /** A short tap. */
  tap(): Promise<void> | void;
  /** One long, heavy tap. */
  longTap(): Promise<void> | void;
  /** The instant "Nice scan!" tick, before any lookup. */
  tick(): Promise<void> | void;
  wait(ms: number): Promise<void>;
};

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

const expoDriver: HapticDriver = {
  async tap() {
    if (Platform.OS === 'web') return;
    const Haptics = await import('expo-haptics');
    await Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  },
  async longTap() {
    if (Platform.OS === 'web') return;
    const Haptics = await import('expo-haptics');
    await Haptics.notificationAsync(Haptics.NotificationFeedbackType.Warning);
  },
  async tick() {
    if (Platform.OS === 'web') return;
    const Haptics = await import('expo-haptics');
    await Haptics.selectionAsync();
  },
  wait: sleep,
};

let driver: HapticDriver = expoDriver;

export const setHapticDriver = (next: HapticDriver | null) => {
  driver = next ?? expoDriver;
};

const safely = async (run: () => Promise<void> | void) => {
  try {
    await run();
  } catch {
    // Haptics are a nicety; a device without them must not break scanning.
  }
};

export const haptics = {
  /** A code was read. Fires at once, with no network involved. */
  scanned: () => safely(() => driver.tick()),
  /** You own this: two short taps. */
  owned: () =>
    safely(async () => {
      await driver.tap();
      await driver.wait(120);
      await driver.tap();
    }),
  /** Found, and not in your library yet: one tap. */
  isNew: () => safely(() => driver.tap()),
  /** The catalogue has nothing for this code: one long tap. */
  notFound: () => safely(() => driver.longTap()),
};
