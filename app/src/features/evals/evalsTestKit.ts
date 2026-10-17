// Test pieces shared by the eval runner and screen tests: the real selector and Vision text engine over a fake vision engine,
// a fake file system that records every download and delete, and a config store whose server answer the test can change.
import { evalApi } from './api';
import { createEvalRunner, type EvalRunner } from './runner';
import { RecognitionConfigStore } from '@/features/scan/recognition/config';
import { DEFAULT_RECOGNITION_CONFIG, type RecognitionConfig } from '@/features/scan/recognition/defaults';
import { EngineSelector } from '@/features/scan/recognition/EngineSelector';
import { failedRead, line, modelItem, okRead, RecordingVision, testIds, type Scene } from '@/features/scan/recognition/fakes';
import { VisionPipelineEngine } from '@/features/scan/recognition/VisionPipelineEngine';
import { setPictureFiles, trackPicture } from '@/features/scan/pictureFiles';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { evalsMock, goldenPhotos } from '@/mocks/evals';

export const uriOf = (id: string) => `file:///cache/cm-evals/${id}.jpg`;

/** What the fake vision engine sees for a golden photo: two items by the model, two lines of text for Vision. */
export const sceneFor = (id: string, overrides: Partial<Scene> = {}): Scene => ({
  read: okRead([modelItem(`Title of ${id}`, `Title of ${id}`, 0), modelItem(`Other of ${id}`, `Other of ${id}`, 1)]),
  text: [line(`Title of ${id}`, 0.1, 0.1, 0.1, 0.5), line(`Other of ${id}`, 0.5, 0.1, 0.1, 0.5)],
  ...overrides,
});

export interface KitOptions {
  photos?: number;
  /** Per photo id: replace parts of its scene. */
  scenes?: Record<string, Partial<Scene>>;
  /** Server config versions: the n-th refresh gets the n-th version (the last one repeats). */
  versions?: string[];
  /** Makes the download of this photo fail. */
  failDownload?: string[];
  /** Makes the selector (the Apple model side) throw instead of reading. */
  selectorThrows?: boolean;
  /** Called each time the fake vision engine is asked to read, to pause or cancel in the middle of a run. */
  onRead?: (uri: string) => void | Promise<void>;
}

export function makeKit(options: KitOptions = {}) {
  evalsMock.access = 'enabled';
  evalsMock.photos = goldenPhotos(options.photos ?? 4);
  // the runner reads shelf photos before single ones, each set in id order
  const ids = [...evalsMock.photos].sort((a, b) => (a.set === b.set ? a.id.localeCompare(b.id) : a.set.localeCompare(b.set))).map((p) => p.id);

  const scenes = Object.fromEntries(ids.map((id) => [uriOf(id), sceneFor(id, options.scenes?.[id])]));
  class Vision extends RecordingVision {
    override async readItems(...args: Parameters<RecordingVision['readItems']>) {
      await options.onRead?.(args[0]);
      return super.readItems(...args);
    }
  }
  const vision = new Vision(scenes);

  const deleted: string[] = [];
  const downloaded: string[] = [];
  setPictureFiles({
    storage: new MemoryKeyValueStore(),
    remove: async (uri) => {
      deleted.push(uri);
    },
  });
  const files = {
    async download(id: string) {
      if (options.failDownload?.includes(id)) throw new Error('download failed');
      downloaded.push(id);
      return trackPicture(uriOf(id));
    },
    async sizeOf() {
      return { width: 3000, height: 4000 };
    },
  };

  const versions = options.versions ?? ['v1'];
  let refreshes = 0;
  const configFor = (version: string): RecognitionConfig => ({
    ...DEFAULT_RECOGNITION_CONFIG,
    version,
    prompt: { template: `Prompt for ${version}.`, outputRules: `Rules for ${version}.` },
    engine: { modelTimeoutMs: 1234 + versions.indexOf(version), maxItemsPerSnap: 17 },
  });
  const store = new RecognitionConfigStore({
    storage: new MemoryKeyValueStore(),
    fetcher: async () => {
      const version = versions[Math.min(refreshes, versions.length - 1)];
      refreshes += 1;
      return { status: 'ok', etag: version, body: configFor(version) };
    },
  });
  const settings = () => store.current();
  const awake = { on: 0, off: 0 };

  const runner: EvalRunner = createEvalRunner({
    api: evalApi,
    files,
    selector: options.selectorThrows
      ? {
          read: async () => {
            throw new Error('the reader broke');
          },
        }
      : new EngineSelector({ vision, settings, newId: testIds }),
    pipeline: new VisionPipelineEngine({ vision, settings, newId: testIds }),
    config: store,
    vision,
    device: { model: 'iPhone16,1', appVersion: '1.2.3' },
    keepAwake: {
      on: async () => {
        awake.on += 1;
      },
      off: async () => {
        awake.off += 1;
      },
    },
  });
  return { runner, vision, store, ids, deleted, downloaded, awake, configFor };
}

export { failedRead };
