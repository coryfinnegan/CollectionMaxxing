// iOS entry. The web build resolves index.web.ts instead and never bundles this file.
import { requireNativeModule } from 'expo';
import type { AppIconEngine } from './types';

export * from './types';
export { FakeAppIconEngine } from './FakeAppIconEngine';
export { UnavailableAppIconEngine } from './UnavailableAppIconEngine';

/** What ios/CmAppIconModule.swift exposes. */
interface NativeCmAppIcon {
  supportsAlternateIcons(): Promise<boolean>;
  getIconName(): Promise<string | null>;
  setIconName(name: string | null): Promise<void>;
}

export class NativeAppIconEngine implements AppIconEngine {
  private module: NativeCmAppIcon | undefined;

  private native(): NativeCmAppIcon {
    this.module ??= requireNativeModule<NativeCmAppIcon>('CmAppIcon');
    return this.module;
  }

  async supportsAlternateIcons(): Promise<boolean> {
    try {
      return await this.native().supportsAlternateIcons();
    } catch {
      return false; // e.g. Expo Go, which does not contain the module
    }
  }

  async currentIconName(): Promise<string | null> {
    try {
      return await this.native().getIconName();
    } catch {
      return null;
    }
  }

  async setIconName(name: string | null): Promise<void> {
    await this.native().setIconName(name); // failures reject, so the UI can say the switch failed
  }
}

export const appIcon: AppIconEngine = new NativeAppIconEngine();
