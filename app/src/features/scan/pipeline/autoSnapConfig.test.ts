import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { DEFAULT_RECOGNITION_CONFIG } from '../recognition/defaults';
import { HASH_BITS, toAutoSnapConfig } from './autoSnapConfig';

describe('auto-snap thresholds from the recognition config', () => {
  it('takes every threshold the server owns', () => {
    const config = toAutoSnapConfig({ steadyMs: 900, minSharpness: 0.6, minTextBoxes: 3, minRectangleCoverage: 0.3, cooldownHashDistance: 16, misfireRaise: 0.25 });
    expect(config).toMatchObject({ steadyMs: 900, minSharpness: 0.6, minTextBoxes: 3, minRectangleCoverage: 0.3, misfireRaise: 0.25 });
    expect(config.minViewDistance).toBe(16 / HASH_BITS);
  });

  it('keeps the machine its own values for what the server does not send', () => {
    const config = toAutoSnapConfig(DEFAULT_RECOGNITION_CONFIG.autoSnap);
    expect(config.cooldownMs).toBe(DEFAULT_AUTO_SNAP_CONFIG.cooldownMs);
    expect(config.misfireStreak).toBe(DEFAULT_AUTO_SNAP_CONFIG.misfireStreak);
    expect(config.maxRaiseSteps).toBe(DEFAULT_AUTO_SNAP_CONFIG.maxRaiseSteps);
    expect(config.hintAfterMs).toBe(DEFAULT_AUTO_SNAP_CONFIG.hintAfterMs);
  });

  it('never asks for a view distance beyond the whole hash', () => {
    expect(toAutoSnapConfig({ ...DEFAULT_RECOGNITION_CONFIG.autoSnap, cooldownHashDistance: 500 }).minViewDistance).toBe(1);
  });
});
