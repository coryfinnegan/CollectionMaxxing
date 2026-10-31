// Whether the scanner may use the camera: its screen is the one being looked at (the tab is selected, nothing is on top of it)
// and the app is in front. Either one going away turns the scanner off.
import { act, renderHook } from '@testing-library/react-native';
import { AppState, type AppStateStatus } from 'react-native';
import { useScreenActive } from './useScreenActive';

const mockFocus = { current: true };
jest.mock('expo-router', () => ({ useIsFocused: () => mockFocus.current }));

let changeAppState: (state: AppStateStatus) => void = () => undefined;
let removed: jest.Mock;

beforeEach(() => {
  mockFocus.current = true;
  removed = jest.fn();
  jest.spyOn(AppState, 'addEventListener').mockImplementation((_type, handler) => {
    changeAppState = handler;
    return { remove: removed };
  });
});
afterEach(() => jest.restoreAllMocks());

describe('useScreenActive', () => {
  it('is active when the screen is focused and the app is in front', async () => {
    const { result } = await renderHook(() => useScreenActive());
    expect(result.current).toBe(true);
  });

  it('is not active when the screen is not focused, and is again when it is', async () => {
    mockFocus.current = false;
    const { result, rerender } = await renderHook(() => useScreenActive());
    expect(result.current).toBe(false);

    mockFocus.current = true;
    await rerender({});
    expect(result.current).toBe(true);
  });

  it.each(['background', 'inactive'] as const)('is not active while the app is %s, and is again when it is back', async (state) => {
    const { result } = await renderHook(() => useScreenActive());

    await act(async () => changeAppState(state));
    expect(result.current).toBe(false);

    await act(async () => changeAppState('active'));
    expect(result.current).toBe(true);
  });

  it('stops listening when the screen goes away', async () => {
    const { unmount } = await renderHook(() => useScreenActive());
    await unmount();
    expect(removed).toHaveBeenCalled();
  });
});
