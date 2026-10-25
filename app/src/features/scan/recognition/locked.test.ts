// LOCKED acceptance tests for NAT-2c (issue #111). Never weaken, skip or delete these.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { AppleModelEngine } from './AppleModelEngine';
import { DEFAULT_RECOGNITION_SETTINGS } from './defaults';
import { EngineSelector } from './EngineSelector';
import { line, modelItem, okRead, RecordingVision, testIds } from './fakes';
import { toIdentifyItem } from './mapping';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const PHOTO = { uri: 'file:///var/mobile/Containers/Data/photo-1.heic', width: 3000, height: 4000 };

function scene() {
  return {
    [PHOTO.uri]: {
      read: okRead([
        modelItem('Dune', 'Dune Frank Herbert', 0, { creator: 'Frank Herbert', kind: 'book' }),
        modelItem('The Matrix', 'THE MATRIX Warner', 1, { kind: 'vhs', completeness: 'boxed' }),
      ]),
      text: [line('Dune', 0.1, 0.1, 0.1, 0.5), line('Frank Herbert', 0.1, 0.65, 0.1, 0.2), line('THE MATRIX', 0.5, 0.1, 0.1, 0.5)],
      rectangles: [{ x: 0.05, y: 0.05, width: 0.3, height: 0.9 }],
    },
  };
}

const deps = (vision: RecordingVision) => ({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds });

describe('privacy: only text and fields ever leave the engines', () => {
  it('serialized readings and IdentifyItems hold no image bytes, data URI or file URI', async () => {
    const vision = new RecordingVision(scene());
    const readings = [
      await new AppleModelEngine(deps(vision)).read(PHOTO, () => {}),
      await new VisionPipelineEngine(deps(vision)).read(PHOTO, () => {}),
      await new EngineSelector(deps(vision)).read(PHOTO, () => {}),
    ];
    const outgoing = readings.flatMap((r) => r.items.map(toIdentifyItem));
    expect(outgoing.length).toBeGreaterThan(0);
    for (const text of [JSON.stringify(readings), JSON.stringify(outgoing)]) {
      expect(text).not.toContain(PHOTO.uri);
      expect(text).not.toMatch(/data:|file:|base64|content:|\.heic|\.jpe?g|\.png/i);
    }
  });
});

describe('the model is a hint', () => {
  const READ_FIELDS = new Set([
    'localId', 'text', 'title', 'creator', 'kind', 'format', 'platform', 'edition', 'completeness', 'box', 'order', 'confidence',
  ]);

  it('an Apple model reading carries only read fields and a confidence, never a confirmed match', async () => {
    const reading = await new AppleModelEngine(deps(new RecordingVision(scene()))).read(PHOTO, () => {});
    expect(reading.items.length).toBe(2);
    for (const item of reading.items) {
      expect(Object.keys(item).filter((k) => !READ_FIELDS.has(k))).toEqual([]);
      expect(['high', 'medium', 'low']).toContain(item.confidence);
      expect(toIdentifyItem(item).localMatchReleaseId ?? null).toBeNull();
    }
    expect(JSON.stringify(reading)).not.toMatch(/confirmed|matched|releaseId/i);
  });
});

describe('no tunable value lives outside defaults.ts', () => {
  const folder = __dirname;
  const sources = readdirSync(folder)
    .filter((f) => /\.tsx?$/.test(f) && !/\.test\.tsx?$/.test(f) && f !== 'defaults.ts' && f !== 'fakes.ts')
    .map((f) => ({ f, text: readFileSync(join(folder, f), 'utf8') }));
  // Comments and strings are not thresholds.
  const code = (text: string) =>
    text
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/\/\/.*$/gm, '')
      .replace(/(['"`])(?:\\.|(?!\1)[^\\\n])*\1/g, '""');

  it('has source files to check', () => {
    expect(sources.length).toBeGreaterThan(5);
  });

  it('keeps the default prompt wording in defaults.ts only', () => {
    const words = [DEFAULT_RECOGNITION_SETTINGS.prompt.template, DEFAULT_RECOGNITION_SETTINGS.prompt.outputRules].map((p) => p.slice(0, 30));
    for (const { f, text } of sources) for (const w of words) expect([f, text.includes(w)]).toEqual([f, false]);
  });

  it('writes no numeric threshold outside defaults.ts', () => {
    const allowed = new Set(['0', '1', '2', '90', '270', '1000']);
    for (const { f, text } of sources) {
      const numbers = (code(text).match(/(?<![\w.$])\d+(?:\.\d+)?(?![\w.])/g) ?? []).filter((n) => !allowed.has(n));
      expect([f, numbers]).toEqual([f, []]);
    }
  });
});
