// A single cover's text is read once: the cue and the engine both want the same three Vision passes, and the second one used to queue behind the first.
import { DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { AppleModelEngine } from './AppleModelEngine';
import { withCoverCue } from './CoverCueEngine';
import { line, modelItem, okRead, RecordingVision, STILL, testIds } from './fakes';
import { FmTextEngine } from './FmTextEngine';
import type { RecognitionEngine } from './types';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const scene = {
  read: okRead([modelItem('Dune', 'Dune', 0)]),
  extract: okRead([modelItem('Dune', 'Dune', 0)]),
  text: [line('Dune', 0.2, 0.1, 0.5, 0.1), line('Frank Herbert', 0.2, 0.3, 0.4, 0.05)],
};

/** A RecordingVision that counts the three-pass reads and keeps each one open until the test lets it go, so the cue and the engine are both in flight. */
class CountingVision extends RecordingVision {
  threePassReads = 0;
  release: () => void = () => {};
  private readonly gate = new Promise<void>((resolve) => (this.release = resolve));

  override async recognizeText(...args: Parameters<RecordingVision['recognizeText']>) {
    if (args[1]?.orientations?.length === 3) this.threePassReads += 1;
    await this.gate;
    return super.recognizeText(...args);
  }
}

const tiers: [string, (vision: CountingVision) => RecognitionEngine][] = [
  ['the model', (vision) => new AppleModelEngine({ vision, settings: () => S, newId: testIds })],
  ['fm_text', (vision) => new FmTextEngine({ vision, settings: () => S, newId: testIds })],
  ['Vision', (vision) => new VisionPipelineEngine({ vision, settings: () => S, newId: testIds })],
];

describe('a single cover read through the cover cue', () => {
  it.each(tiers)('asks Vision for its three passes once on the %s tier', async (_name, make) => {
    const vision = new CountingVision({ [STILL.uri]: scene });
    const engine = withCoverCue(make(vision), { vision, settings: () => S });
    const reading = engine.read(STILL, () => {}, { shotKind: 'single_cover' });
    // Let the cue and the engine both reach Vision before it answers.
    for (let i = 0; i < 20; i++) await Promise.resolve();
    vision.release();
    await reading;
    expect(vision.threePassReads).toBe(1);
  });
});
