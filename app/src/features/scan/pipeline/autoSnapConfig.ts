// The server owns auto-snap's thresholds (GET /recognition/config, cached on the phone by NAT-2c). The machine takes an
// `AutoSnapConfig`; this is the one place the two are joined. Values the server does not send keep the machine's defaults.
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import type { AutoSnapConfig } from '../autosnap/types';
import type { RecognitionConfig } from '../recognition/defaults';

/** The view hash is 64 bits (autosnap/hash.ts); the server counts differing bits, the machine a share of them. */
export const HASH_BITS = 64;

export function toAutoSnapConfig(server: RecognitionConfig['autoSnap'], base: AutoSnapConfig = DEFAULT_AUTO_SNAP_CONFIG): AutoSnapConfig {
  return {
    ...base,
    steadyMs: server.steadyMs,
    minSharpness: server.minSharpness,
    minTextBoxes: server.minTextBoxes,
    minRectangleCoverage: server.minRectangleCoverage,
    minViewDistance: Math.min(1, server.cooldownHashDistance / HASH_BITS),
    misfireRaise: server.misfireRaise,
  };
}
