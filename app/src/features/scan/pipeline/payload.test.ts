// Spike #505: the /identify payload stops fm_text readings until the server knows that engine.
import { toScanEngine } from './payload';

describe('toScanEngine', () => {
  it.each(['apple_model', 'vision_pipeline'] as const)('passes %s through to the server', (engine) => {
    expect(toScanEngine(engine)).toBe(engine);
  });

  it('refuses fm_text readings rather than mislabelling them', () => {
    expect(() => toScanEngine('fm_text')).toThrow(/fm_text.*spike #505/);
  });
});
