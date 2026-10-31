// Whether the scanner may use the camera right now. The tab bar keeps every tab mounted (docs.expo.dev "Native tabs": tab screens
// render eagerly), so a screen that is not showing is still alive; the scanner has to know it is not being looked at, or the
// phone goes on reading barcodes and taking pictures behind another tab, or with the app in the background.
import { useIsFocused } from 'expo-router';
import { useEffect, useState } from 'react';
import { AppState, type AppStateStatus } from 'react-native';

const inFront = (state: AppStateStatus) => state !== 'background' && state !== 'inactive';

/** The app is the one in front (not in the background, not covered by the app switcher or a system sheet). */
export function useAppInFront(): boolean {
  const [front, setFront] = useState(() => inFront(AppState.currentState));
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (state) => setFront(inFront(state)));
    return () => subscription.remove();
  }, []);
  return front;
}

/** True while this screen is the focused one and the app is in front. */
export function useScreenActive(): boolean {
  const focused = useIsFocused();
  const front = useAppInFront();
  return focused && front;
}
