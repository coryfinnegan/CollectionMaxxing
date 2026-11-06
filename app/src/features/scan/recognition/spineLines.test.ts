// A shelf read sends its spine's lines (#725), so the server can ask another line of a spine when the first finds nothing (#724).
// A cover read and a model-only read send none: a cover's leftover lines are noise, and the model has no lines.
import { AppleModelEngine } from './AppleModelEngine';
import { DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { toIdentifyItem } from './mapping';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const pipelineOf = (scene: Scene) => new VisionPipelineEngine({ vision: new RecordingVision({ [STILL.uri]: scene }), settings: () => S, newId: testIds });

/** One spine of three lines (one printed twice, as Vision can read it), beside a spine of one. */
const shelf: Scene = {
  text: [
    line('GRAN TURISMO 3', 0.1, 0.1, 0.05, 0.4),
    line('Polyphony Digital', 0.11, 0.55, 0.04, 0.2),
    line('GRAN TURISMO 3', 0.12, 0.1, 0.05, 0.4),
    line('EMMA', 0.5, 0.1, 0.05, 0.6),
  ],
  rectangles: [
    { x: 0.08, y: 0.05, width: 0.09, height: 0.7 },
    { x: 0.48, y: 0.05, width: 0.09, height: 0.7 },
  ],
};

describe('a read sends its spine\'s lines', () => {
  it('gives a shelf read its spine\'s distinct lines, in the spine\'s order', async () => {
    const reading = await pipelineOf(shelf).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => i.lines)).toEqual([['GRAN TURISMO 3', 'Polyphony Digital'], ['EMMA']]);
    expect(toIdentifyItem(reading.items[0]).lines).toEqual(['GRAN TURISMO 3', 'Polyphony Digital']);
  });

  it('caps a spine\'s lines at the server\'s twelve', async () => {
    const many: Scene = {
      text: Array.from({ length: 15 }, (_, i) => line(`Line ${i}`, 0.1, 0.05 + i * 0.05, 0.05, 0.04)),
      rectangles: [{ x: 0.08, y: 0.03, width: 0.1, height: 0.9 }],
    };
    const reading = await pipelineOf(many).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].lines).toHaveLength(12);
  });

  it('gives a cover read none', async () => {
    const reading = await pipelineOf(shelf).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0]).not.toHaveProperty('lines');
    expect(toIdentifyItem(reading.items[0])).not.toHaveProperty('lines');
  });

  it('gives a model-only read none', async () => {
    const scene: Scene = { read: okRead([modelItem('Dune', 'Dune', 0)]), text: [line('Dune', 0.1, 0.1, 0.1, 0.5)] };
    const reading = await new AppleModelEngine({ vision: new RecordingVision({ [STILL.uri]: scene }), settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.length).toBeGreaterThan(0);
    for (const item of reading.items) {
      expect(item).not.toHaveProperty('lines');
      expect(toIdentifyItem(item)).not.toHaveProperty('lines');
    }
  });
});
