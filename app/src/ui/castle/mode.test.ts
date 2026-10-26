// The castle render mode: the Theme setting (Full/Quiet) combined with the system
// Increase Contrast and Reduce Motion signals; Accessible wins on conflict.
import { renderHook, waitFor } from '@testing-library/react-native';
import { AccessibilityInfo, Platform } from 'react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { resolveCastleMode, setCastleStorage, useCastleMode, useCastleThemeSetting, useIncreaseContrast } from './mode';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = useReduceMotion as jest.Mock;

const addListener = jest.spyOn(AccessibilityInfo, 'addEventListener');
const darkerColors = jest.spyOn(AccessibilityInfo, 'isDarkerSystemColorsEnabled');
const highText = jest.spyOn(AccessibilityInfo, 'isHighTextContrastEnabled');
type Sub = ReturnType<typeof AccessibilityInfo.addEventListener>;
const fakeSub = () => ({ remove: jest.fn() }) as unknown as Sub;

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('resolveCastleMode', () => {
  it.each([
    ['full', false, false, 'full'],
    ['quiet', false, false, 'flat'],
    ['full', true, false, 'accessible'],
    ['quiet', true, false, 'accessible'],
    ['full', false, true, 'accessible'],
    ['quiet', false, true, 'accessible'],
    ['full', true, true, 'accessible'],
    ['quiet', true, true, 'accessible'],
  ] as const)('setting %s, contrast %s, motion %s renders %s', (setting, increaseContrast, reduce, mode) => {
    expect(resolveCastleMode(setting, { increaseContrast, reduceMotion: reduce })).toBe(mode);
  });
});

describe('castle theme setting', () => {
  beforeEach(() => {
    setCastleStorage(memoryStore());
    useCastleThemeSetting.setState({ setting: 'full' });
  });

  it('is full until the person picks quiet', () => {
    expect(useCastleThemeSetting.getState().setting).toBe('full');
  });

  it('keeps the choice across reloads', () => {
    useCastleThemeSetting.getState().set('quiet');
    useCastleThemeSetting.setState({ setting: 'full' });
    useCastleThemeSetting.getState().reload();
    expect(useCastleThemeSetting.getState().setting).toBe('quiet');
  });
});

describe('useIncreaseContrast', () => {
  beforeEach(() => {
    addListener.mockReset().mockReturnValue(fakeSub());
    darkerColors.mockReset().mockResolvedValue(false);
    highText.mockReset().mockResolvedValue(false);
  });

  it('follows darker system colors on iOS', async () => {
    jest.replaceProperty(Platform, 'OS', 'ios');
    darkerColors.mockResolvedValue(true);
    const { result } = await renderHook(() => useIncreaseContrast());
    await waitFor(() => expect(result.current).toBe(true));
    expect(addListener).toHaveBeenCalledWith('darkerSystemColorsChanged', expect.any(Function));
  });

  it('follows high text contrast on Android', async () => {
    jest.replaceProperty(Platform, 'OS', 'android');
    highText.mockResolvedValue(true);
    const { result } = await renderHook(() => useIncreaseContrast());
    await waitFor(() => expect(result.current).toBe(true));
    expect(addListener).toHaveBeenCalledWith('highTextContrastChanged', expect.any(Function));
  });

  it('follows prefers-contrast on the web', async () => {
    jest.replaceProperty(Platform, 'OS', 'web');
    let onChange: (matches: boolean) => void = () => {};
    const globals = globalThis as Record<string, unknown>;
    globals.window = {
      matchMedia: jest.fn(() => ({
        matches: false,
        addEventListener: (_event: string, fn: (e: { matches: boolean }) => void) => void (onChange = (matches) => fn({ matches })),
        removeEventListener: jest.fn(),
      })),
    };
    try {
      const { result } = await renderHook(() => useIncreaseContrast());
      expect(result.current).toBe(false);
      onChange(true);
      await waitFor(() => expect(result.current).toBe(true));
    } finally {
      delete globals.window;
    }
  });

  it('stays off when the platform answers nothing', async () => {
    jest.replaceProperty(Platform, 'OS', 'ios');
    darkerColors.mockRejectedValue(new Error('no module'));
    const { result } = await renderHook(() => useIncreaseContrast());
    await waitFor(() => expect(addListener).toHaveBeenCalled());
    expect(result.current).toBe(false);
  });
});

describe('useCastleMode', () => {
  beforeEach(() => {
    setCastleStorage(memoryStore());
    useCastleThemeSetting.setState({ setting: 'full' });
    reduceMotion.mockReset().mockReturnValue(false);
    jest.replaceProperty(Platform, 'OS', 'ios');
    addListener.mockReset().mockReturnValue(fakeSub());
    darkerColors.mockReset().mockResolvedValue(false);
  });

  it('renders full by default', async () => {
    const { result } = await renderHook(() => useCastleMode());
    await waitFor(() => expect(result.current).toBe('full'));
  });

  it('renders flat when the person picked quiet', async () => {
    useCastleThemeSetting.getState().set('quiet');
    const { result } = await renderHook(() => useCastleMode());
    await waitFor(() => expect(result.current).toBe('flat'));
  });

  it('renders accessible when the system asks, even on full', async () => {
    darkerColors.mockResolvedValue(true);
    const { result } = await renderHook(() => useCastleMode());
    await waitFor(() => expect(result.current).toBe('accessible'));
  });
});
