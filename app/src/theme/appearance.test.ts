// The display appearance choice (#363): System until the person pins Light or
// Dark, applied to the styled layer and resolved to the effective scheme.
import { renderHook } from '@testing-library/react-native';
import { useColorScheme } from 'react-native';
import { applyAppearance, setAppearanceStorage, useAppearanceChoice, useEffectiveScheme } from './appearance';

jest.mock('react-native/Libraries/Utilities/useColorScheme', () => ({ __esModule: true, default: jest.fn() }));
const systemScheme = useColorScheme as jest.Mock;

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('appearance choice', () => {
  beforeEach(() => {
    setAppearanceStorage(memoryStore());
    useAppearanceChoice.setState({ choice: 'system' });
  });

  it('follows the system until the person picks', () => {
    expect(useAppearanceChoice.getState().choice).toBe('system');
  });

  it('keeps the choice across reloads', () => {
    useAppearanceChoice.getState().set('dark');
    useAppearanceChoice.setState({ choice: 'system' });
    useAppearanceChoice.getState().reload();
    expect(useAppearanceChoice.getState().choice).toBe('dark');
  });

  it('falls back to the system when the stored value is unknown', () => {
    setAppearanceStorage({ getString: () => 'sepia', set: () => undefined });
    useAppearanceChoice.getState().reload();
    expect(useAppearanceChoice.getState().choice).toBe('system');
  });
});

describe('useEffectiveScheme', () => {
  beforeEach(() => {
    setAppearanceStorage(memoryStore());
    useAppearanceChoice.setState({ choice: 'system' });
    systemScheme.mockReset().mockReturnValue('light');
  });

  it('follows the system scheme while the choice is system', async () => {
    systemScheme.mockReturnValue('dark');
    const { result } = await renderHook(() => useEffectiveScheme());
    expect(result.current).toBe('dark');
  });

  it('reads light while the system scheme is still loading', async () => {
    systemScheme.mockReturnValue(null);
    const { result } = await renderHook(() => useEffectiveScheme());
    expect(result.current).toBe('light');
  });

  it('pins light even when the system is dark', async () => {
    systemScheme.mockReturnValue('dark');
    useAppearanceChoice.setState({ choice: 'light' });
    const { result } = await renderHook(() => useEffectiveScheme());
    expect(result.current).toBe('light');
  });

  it('pins dark even when the system is light', async () => {
    systemScheme.mockReturnValue('light');
    useAppearanceChoice.setState({ choice: 'dark' });
    const { result } = await renderHook(() => useEffectiveScheme());
    expect(result.current).toBe('dark');
  });
});

describe('applyAppearance', () => {
  it('pins the styled layer to the chosen theme', () => {
    const runtime = { setTheme: jest.fn(), setAdaptiveThemes: jest.fn() };
    applyAppearance('dark', runtime);
    expect(runtime.setAdaptiveThemes).toHaveBeenCalledWith(false);
    expect(runtime.setTheme).toHaveBeenCalledWith('dark');
  });

  it('hands the styled layer back to the OS for system', () => {
    const runtime = { setTheme: jest.fn(), setAdaptiveThemes: jest.fn() };
    applyAppearance('system', runtime);
    expect(runtime.setAdaptiveThemes).toHaveBeenCalledWith(true);
    expect(runtime.setTheme).not.toHaveBeenCalled();
  });
});
