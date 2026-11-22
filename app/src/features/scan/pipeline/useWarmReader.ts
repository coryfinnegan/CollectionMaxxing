// Wakes the shelf reader while the scanner is showing (#832). Called by the scanner route with whether the screen is active.
import { useEffect } from 'react';
import { getRecognition } from '../recognition/device';
import { appReaderWarmer, type ReaderWarmer } from './warmReader';

export function useWarmReader(active: boolean, warmer?: ReaderWarmer): void {
  useEffect(() => {
    if (!active) return undefined;
    const current = warmer ?? appReaderWarmer(() => getRecognition().store.shelfReader());
    current.opened();
    return () => current.closed();
  }, [active, warmer]);
}
