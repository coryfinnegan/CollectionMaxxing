// cm-app-icon: switching the home screen icon between the default dragon and the
// Game Boy alternate (#364).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { FakeAppIconEngine } from '../FakeAppIconEngine';
import { NativeAppIconEngine } from '../index';
import type { AppIconEngine } from '../types';
import { UnavailableAppIconEngine } from '../UnavailableAppIconEngine';

const mockRequireNativeModule = jest.fn();
jest.mock('expo', () => ({ requireNativeModule: (...args: unknown[]) => mockRequireNativeModule(...args) }));

const iosDir = join(__dirname, '..', '..', 'ios');

describe('FakeAppIconEngine', () => {
  it('starts on the default icon with alternates supported', async () => {
    const engine = new FakeAppIconEngine();
    await expect(engine.supportsAlternateIcons()).resolves.toBe(true);
    await expect(engine.currentIconName()).resolves.toBeNull();
  });

  it('switches to the alternate and back, recording each switch', async () => {
    const engine = new FakeAppIconEngine();
    await engine.setIconName('gameboy');
    await expect(engine.currentIconName()).resolves.toBe('gameboy');
    await engine.setIconName(null);
    await expect(engine.currentIconName()).resolves.toBeNull();
    expect(engine.applied).toEqual(['gameboy', null]);
  });

  it('starts on a given icon, for tests that open mid-switch', async () => {
    const engine = new FakeAppIconEngine('gameboy');
    await expect(engine.currentIconName()).resolves.toBe('gameboy');
  });

  it('an unsupported fake refuses to switch', async () => {
    const engine = new FakeAppIconEngine(null, false);
    await expect(engine.supportsAlternateIcons()).resolves.toBe(false);
    await expect(engine.setIconName('gameboy')).rejects.toThrow('not supported');
  });
});

describe('UnavailableAppIconEngine', () => {
  const engine: AppIconEngine = new UnavailableAppIconEngine();

  it('reports no alternates, the default icon, and refuses to switch', async () => {
    await expect(engine.supportsAlternateIcons()).resolves.toBe(false);
    await expect(engine.currentIconName()).resolves.toBeNull();
    await expect(engine.setIconName('gameboy')).rejects.toThrow('not available');
  });
});

describe('NativeAppIconEngine', () => {
  beforeEach(() => mockRequireNativeModule.mockReset());

  it('asks the CmAppIcon module and reports its answers', async () => {
    const native = { supportsAlternateIcons: jest.fn().mockResolvedValue(true), getIconName: jest.fn().mockResolvedValue('gameboy'), setIconName: jest.fn().mockResolvedValue(undefined) };
    mockRequireNativeModule.mockReturnValue(native);
    const engine = new NativeAppIconEngine();

    await expect(engine.supportsAlternateIcons()).resolves.toBe(true);
    await expect(engine.currentIconName()).resolves.toBe('gameboy');
    await engine.setIconName(null);

    expect(mockRequireNativeModule).toHaveBeenCalledWith('CmAppIcon');
    expect(native.setIconName).toHaveBeenCalledWith(null);
  });

  it('reports no alternates and the default icon when the module is missing (Expo Go, tests)', async () => {
    mockRequireNativeModule.mockImplementation(() => {
      throw new Error('no such module');
    });
    const engine = new NativeAppIconEngine();

    await expect(engine.supportsAlternateIcons()).resolves.toBe(false);
    await expect(engine.currentIconName()).resolves.toBeNull();
  });

  it('rejects the switch when the module is missing, so the UI can say so', async () => {
    mockRequireNativeModule.mockImplementation(() => {
      throw new Error('no such module');
    });
    await expect(new NativeAppIconEngine().setIconName('gameboy')).rejects.toThrow('no such module');
  });
});

describe('cm-app-icon native wiring', () => {
  const swift = () => readFileSync(join(iosDir, 'CmAppIconModule.swift'), 'utf8');

  it('exposes the three functions TypeScript calls, on the main thread (UIKit hangs them off the shared application)', () => {
    expect(swift()).toMatch('Name("CmAppIcon")');
    for (const name of ['supportsAlternateIcons', 'getIconName', 'setIconName']) {
      expect(swift()).toMatch(`AsyncFunction("${name}")`);
    }
    expect(swift()).toMatch('DispatchQueue.main.async');
    expect(swift()).toMatch('setAlternateIconName');
  });

  it('registers the module class and pod names autolinking looks for', () => {
    expect(JSON.parse(readFileSync(join(iosDir, '..', 'expo-module.config.json'), 'utf8'))).toEqual({
      platforms: ['apple'],
      apple: { modules: ['CmAppIconModule'] },
    });
    expect(readFileSync(join(iosDir, 'CmAppIcon.podspec'), 'utf8')).toMatch("s.name           = 'CmAppIcon'");
  });
});
