import { act, renderHook } from '@testing-library/react-native';
import { useNow } from './useNow';

describe('useNow', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('starts at the current time', async () => {
    const { result } = await renderHook(() => useNow(60_000));

    expect(result.current).toBe(Date.now());
  });

  it('ticks forward while mounted', async () => {
    const { result } = await renderHook(() => useNow(1_000));
    const first = result.current;

    await act(() => jest.advanceTimersByTime(5_000));

    expect(result.current).toBe(first + 5_000);
  });

  it('clears its timer on unmount', async () => {
    const clearSpy = jest.spyOn(global, 'clearInterval');
    try {
      const { unmount } = await renderHook(() => useNow(1_000));
      await act(async () => {
        unmount();
      });

      expect(clearSpy).toHaveBeenCalledTimes(1);
    } finally {
      clearSpy.mockRestore();
    }
  });
});
