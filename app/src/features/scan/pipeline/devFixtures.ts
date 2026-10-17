// A recorded snap for the dev menu, like `devCodes.ts` is for barcodes: "Snap a fixture shelf" plays this reading through the
// whole pipeline (receipt lines, identify, SSE, cards, a close call) with no camera and no model. The titles match the
// entries in app/src/mocks/identify.ts, so every kind of answer shows up.
import type { ReadItem, SnapReading } from '../recognition/types';
import type { Reader } from './SnapPipeline';

export const DEV_FIXTURE_PHOTO = { uri: 'fixture://shelf.jpg', width: 3000, height: 2000 };

let replays = 0;

const item = (n: number, id: string, title: string, extra: Partial<ReadItem> = {}): ReadItem => ({
  localId: `${id}-${n}`,
  text: title,
  title,
  kind: 'game',
  order: 0,
  confidence: 'high',
  ...extra,
});

/** The recorded reading: an owned book, a new cart, a close call, a slow one, and something the catalogue does not know. */
export function devFixtureReading(replay: number): SnapReading {
  const id = `dev-${replay}`;
  const items = [
    item(1, id, 'Dune', { kind: 'book', order: 0, box: { x: 0.02, y: 0.1, width: 0.12, height: 0.7 } }),
    item(2, id, 'Super Mario 64', { order: 1, platform: 'Nintendo 64', completeness: 'cart_only', box: { x: 0.18, y: 0.2, width: 0.1, height: 0.2 } }),
    item(3, id, 'Pokemon Yellow', { order: 2, platform: 'Game Boy', box: { x: 0.32, y: 0.2, width: 0.1, height: 0.2 } }),
    item(4, id, 'Zelda: Ocarina of Time', { order: 3, platform: 'Nintendo 64', box: { x: 0.46, y: 0.2, width: 0.1, height: 0.2 } }),
    item(5, id, 'Space Mutiny', { kind: 'film', format: 'vhs', order: 4, box: { x: 0.6, y: 0.1, width: 0.12, height: 0.7 } }),
  ];
  return { items, unreadableCount: 2, engine: 'apple_model', timings: { totalMs: 1200, modelMs: 900, visionMs: 300 } };
}

/** A reader that plays the recording, streaming the items one after another. */
export function devFixtureReader(): Reader {
  replays += 1;
  const reading = devFixtureReading(replays);
  return {
    async read(_still, onItem) {
      for (const read of reading.items) onItem(read);
      return reading;
    },
  };
}
