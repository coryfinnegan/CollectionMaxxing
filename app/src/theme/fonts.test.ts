// The loader covers every family the type scale names, so a scale entry can never
// point at a font that was not loaded (it would silently fall back to system).
import { loadAsync } from 'expo-font';
import { renderHook, waitFor } from '@testing-library/react-native';
import { typeScale } from './tokens';
import { fontAssets, useAppFonts } from './fonts';

jest.mock('expo-font', () => {
  const React = jest.requireActual<typeof import('react')>('react');
  const loadAsync = jest.fn().mockResolvedValue(undefined);
  return {
    loadAsync,
    useFonts: (map: unknown) => {
      const [loaded, setLoaded] = React.useState(false);
      React.useEffect(() => {
        loadAsync(map).then(() => setLoaded(true));
      }, [map]);
      return [loaded];
    },
  };
});

describe('app fonts', () => {
  it('loads every family the type scale uses', () => {
    const families = new Set(Object.values(typeScale).map((v) => (v as { fontFamily: string }).fontFamily));
    for (const family of families) expect(fontAssets).toHaveProperty(family);
  });

  it('reports loaded once expo-font finishes', async () => {
    const { result } = await renderHook(() => useAppFonts());
    await waitFor(() => expect(result.current).toBe(true));
    expect(loadAsync).toHaveBeenCalledWith(fontAssets);
  });
});
