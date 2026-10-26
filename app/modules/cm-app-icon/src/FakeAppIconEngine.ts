import type { AppIconEngine } from './types';

/** Remembers the icon in memory and records every switch, for tests. */
export class FakeAppIconEngine implements AppIconEngine {
  private current: string | null;
  readonly applied: (string | null)[] = [];

  constructor(initial: string | null = null, private readonly supported = true) {
    this.current = initial;
  }

  async supportsAlternateIcons(): Promise<boolean> {
    return this.supported;
  }

  async currentIconName(): Promise<string | null> {
    return this.current;
  }

  async setIconName(name: string | null): Promise<void> {
    if (!this.supported) throw new Error('FakeAppIconEngine: alternate icons are not supported');
    this.applied.push(name);
    this.current = name;
  }
}
