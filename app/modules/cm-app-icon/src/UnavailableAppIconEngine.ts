import type { AppIconEngine } from './types';

const message = 'Alternate app icons are not available on the web. The icon switcher lives in the iPhone app only.';

/** Web has no home screen icon to switch; every call says so. */
export class UnavailableAppIconEngine implements AppIconEngine {
  async supportsAlternateIcons(): Promise<boolean> {
    return false;
  }
  async currentIconName(): Promise<string | null> {
    return null;
  }
  async setIconName(): Promise<never> {
    throw new Error(message);
  }
}
