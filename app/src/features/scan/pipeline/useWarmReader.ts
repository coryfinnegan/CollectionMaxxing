// Wakes the shelf reader while the scanner is showing (#832). Called by the scanner route with whether the screen is active.
import { useEffect, useRef } from 'react';
import { getRecognition } from '../recognition/device';
import { readerWarmer, type ReaderWarmer } from './warmReader';

export function useWarmReader(active: boolean, warmer?: ReaderWarmer): void {
  // One warmer for the life of the screen: it remembers when the scanner was last closed.
  const ref = useRef<ReaderWarmer | undefined>(warmer);
  ref.current ??= readerWarmer(() => getRecognition().store.shelfReader());
  useEffect(() => {
    const current = ref.current;
    if (!current || !active) return undefined;
    current.opened();
    return () => current.closed();
  }, [active]);
}
