// Test doubles for the picture pipeline: a reader that returns scripted readings, crops and artwork that need no phone, and
// the stores wired together. Tests build one kit per test and tear it down with `resetKit`.
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import type { FeaturePrint, Rect } from '@/native/cm-vision';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import type { ReadItem, SnapReading, Still } from '../recognition/types';
import type { ArtDeps } from './artStep';
import { restPipelineApi } from './pipelineApi';
import { createPipelineStore, setPipelineStore } from './pipelineStore';
import { MemorySnapFiles, setSnapStore, SnapStore } from './snapStore';
import type { CropMaker, PipelineDeps, Reader } from './SnapPipeline';

export const PHOTO = { uri: 'file:///cache/ImageManipulator/still-1.jpg', width: 2000, height: 1500 };

export const readItem = (title: string, extra: Partial<ReadItem> = {}): ReadItem => ({
  localId: `local-${title.toLowerCase().replace(/\W+/g, '-')}`,
  text: title,
  title,
  kind: 'book',
  order: 0,
  confidence: 'high',
  ...extra,
});

export const readingOf = (items: ReadItem[], extra: Partial<SnapReading> = {}): SnapReading => ({
  items,
  unreadableCount: 0,
  engine: 'apple_model',
  timings: { totalMs: 10, visionMs: 5 },
  ...extra,
});

/** Returns the scripted readings one after another and remembers what it was asked to read. */
export class FakeReader implements Reader {
  readonly stills: Still[] = [];
  private readonly queue: SnapReading[] = [];
  /** Hold every read until this settles. */
  hold: Promise<void> | null = null;

  script(...readings: SnapReading[]) {
    this.queue.push(...readings);
    return this;
  }

  async read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> {
    this.stills.push(still);
    const next = this.queue.shift() ?? readingOf([]);
    for (const item of next.items) onItem(item);
    if (this.hold) await this.hold;
    return next;
  }
}

/** Feature prints that carry the file they came from, and distances a test sets per artwork file. */
export class FakeArt {
  readonly downloads: { url: string; headers: Record<string, string> }[] = [];
  readonly discarded: string[] = [];
  readonly printed: { uri: string; box?: Rect }[] = [];
  /** Distance to the artwork whose file name ends with this id; 0.9 when not set. */
  readonly distances = new Map<string, number>();
  failDownloads = false;

  readonly deps: ArtDeps = {
    vision: {
      featurePrint: async (uri, box): Promise<FeaturePrint> => {
        this.printed.push({ uri, ...(box ? { box } : {}) });
        return { revision: 1, data: uri };
      },
      featurePrintDistance: async (_a, b) => {
        const id = [...this.distances.keys()].find((key) => b.data.endsWith(key));
        return id ? (this.distances.get(id) as number) : 0.9;
      },
    },
    download: async (request) => {
      this.downloads.push(request);
      if (this.failDownloads) throw new Error('offline');
      const response = await fetch(request.url, { headers: request.headers });
      if (!response.ok) throw new Error(`artwork ${response.status}`);
      return `file:///cache/cm-art/${request.url.split('/').pop()}`;
    },
    discard: async (uri) => void this.discarded.push(uri),
  };
}

export class FakeCrops implements CropMaker {
  readonly made: Rect[] = [];
  async thumb(still: Still, box: Rect) {
    this.made.push(box);
    return `file:///cache/ImageManipulator/crop-${this.made.length}-of-${still.uri.split('/').pop()}`;
  }
}

let ids = 0;
const nextSnapId = () => `00000000-0000-4000-8000-${String(++ids).padStart(12, '0')}`;

export function makeKit(overrides: Partial<PipelineDeps> = {}) {
  const tray = createTrayStore({ storage: new MemoryKeyValueStore(), newKey: nextSnapId, now: () => 1_000 });
  setTrayStore(tray);
  const store = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(store);
  const files = new MemorySnapFiles();
  const snaps = new SnapStore({ files, storage: new MemoryKeyValueStore() });
  setSnapStore(snaps);
  const reader = new FakeReader();
  const art = new FakeArt();
  const crops = new FakeCrops();
  const deps: PipelineDeps = {
    libraryId: MOCK_LIBRARY_ID,
    tray,
    store,
    snaps,
    recognition: reader,
    api: restPipelineApi,
    crops,
    art: art.deps,
    newId: nextSnapId,
    ...overrides,
  };
  return { deps, tray, store, files, snaps, reader, art, crops };
}

export function resetKit() {
  setTrayStore(undefined);
  setPipelineStore(undefined);
  setSnapStore(undefined);
}

export interface SeenRequest {
  method: string;
  url: string;
  contentType: string | null;
  body: string;
}

/** Records every request the mock server receives, with its body text, so a test can look at what left the phone. */
export function recordRequests() {
  const seen: SeenRequest[] = [];
  const pending: Promise<void>[] = [];
  const listener = ({ request }: { request: Request }) => {
    const copy = request.clone();
    pending.push(
      copy.text().then((body) => {
        seen.push({ method: request.method, url: request.url, contentType: request.headers.get('Content-Type'), body });
      }),
    );
  };
  server.events.on('request:start', listener);
  return {
    async all() {
      await Promise.all(pending);
      return seen;
    },
    stop: () => void server.events.removeListener('request:start', listener),
  };
}
