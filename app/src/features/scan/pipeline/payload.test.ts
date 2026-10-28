// The /identify payload sends the reading's engine to the server as named.
import { toScanEngine } from './payload';

describe('toScanEngine', () => {
  it.each(['apple_model', 'fm_text', 'vision_pipeline'] as const)('passes %s through to the server', (engine) => {
    expect(toScanEngine(engine)).toBe(engine);
  });
});
