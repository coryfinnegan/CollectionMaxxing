// Auto-snap's thresholds from the recognition config the phone caches (NAT-2c). The scanner opens at once with what is cached
// (or the built-in values); a fresher config from the server applies as soon as it arrives.
import { useEffect, useState } from 'react';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import type { AutoSnapConfig } from '../autosnap/types';
import { getRecognition, watchRecognitionConfig } from '../recognition/device';
import { toAutoSnapConfig } from './autoSnapConfig';

export function useAutoSnapConfig(): AutoSnapConfig {
  // Without the native module (Expo Go) the built-in values still let the scanner open.
  const [config, setConfig] = useState(() => {
    try {
      return toAutoSnapConfig(getRecognition().store.current().autoSnap);
    } catch {
      return DEFAULT_AUTO_SNAP_CONFIG;
    }
  });
  useEffect(() => {
    let alive = true;
    let stop = () => undefined as void;
    try {
      stop = watchRecognitionConfig();
      const { store } = getRecognition();
      void store.refresh().then(() => {
        if (alive) setConfig(toAutoSnapConfig(store.current().autoSnap));
      });
    } catch {
      // keep the config we have
    }
    return () => {
      alive = false;
      stop();
    };
  }, []);
  return config;
}
