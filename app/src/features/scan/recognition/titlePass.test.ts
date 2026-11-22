// An item built from Vision's lines keeps the pass that read its title line (0, 90, 180 or 270), so Review can turn the spine strip the way
// the text reads (#850). It stays on the phone: nothing here reaches the server.
import { DEFAULT_RECOGNITION_SETTINGS } from './defaults';
import { line, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { VisionPipelineEngine } from './VisionPipelineEngine';

async function itemsOf(scene: Scene) {
  const vision = new RecordingVision({ [STILL.uri]: { rectangles: [], ...scene } });
  const engine = new VisionPipelineEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds });
  return (await engine.read(STILL, () => {}, { shotKind: 'shelf_segment' })).items;
}

describe('the pass that read an item title', () => {
  it('An item keeps the pass that read its title: a spine printed bottom to top is pass 90', async () => {
    const items = await itemsOf({ text: [], text90: [line('THE LONG WALK', 0.4, 0.1, 0.04, 0.6)] });
    expect(items).toHaveLength(1);
    expect(items[0]!.pass).toBe(90);
  });

  it('keeps pass 270 for a spine printed top to bottom, and 0 for a title that reads upright', async () => {
    const down = await itemsOf({ text: [], text270: [line('THE LONG WALK', 0.4, 0.1, 0.04, 0.6)] });
    expect(down[0]!.pass).toBe(270);
    const flat = await itemsOf({ text: [line('THE LONG WALK', 0.2, 0.5, 0.4, 0.05)] });
    expect(flat[0]!.pass).toBe(0);
  });
});
