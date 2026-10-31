// Test pieces shared by the eval runner and screen tests: the real selector and its tiers over a fake vision engine,
// a fake file system that records every download and delete, a config store whose server answer the test can change,
// a fake app state to send the app to the background and back, and an outbox in memory that survives a relaunch.
import { evalApi } from './api';
import { createEvalRunner, type AppStateSource, type EvalRunner } from './runner';
import { RecognitionConfigStore } from '@/features/scan/recognition/config';
import { DEFAULT_RECOGNITION_CONFIG, type RecognitionConfig } from '@/features/scan/recognition/defaults';
import { EngineSelector } from '@/features/scan/recognition/EngineSelector';
import { failedRead, line, modelItem, okRead, RecordingVision, testIds, type Scene } from '@/features/scan/recognition/fakes';
import { setPictureFiles, trackPicture } from '@/features/scan/pictureFiles';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { evalsMock, goldenPhotos, holdRun } from '@/mocks/evals';

export const uriOf = (id: string) => `file:///cache/cm-evals/${id}.jpg`;

/** What the fake vision engine sees for a golden photo: two items by the model, two lines of text for Vision, and the same two items extracted from that text. */
export const sceneFor = (id: string, overrides: Partial<Scene> = {}): Scene => ({
  read: okRead([modelItem(`Title of ${id}`, `Title of ${id}`, 0), modelItem(`Other of ${id}`, `Other of ${id}`, 1)]),
  extract: okRead([modelItem(`Title of ${id}`, `Title of ${id}`, 0), modelItem(`Other of ${id}`, `Other of ${id}`, 1)]),
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
  /** Called each time the fake vision engine is asked to read, to pause or stop in the middle of a run. */
  onRead?: (uri: string) => void | Promise<void>;
  /** Called each time a photo is sent to the PC, before the request goes out: hold the promise to keep the send pending, reject it to fail the send. */
  onSend?: (photoId: string) => void | Promise<void>;
  /** The PC already holds a run with these photos stored (as if an earlier session had read them). */
  held?: string[];
  /** Called each time the runner waits (a send that failed, or the PC scoring), with how long it asked to wait: hold the promise to keep it waiting. */
  onSleep?: (ms: number) => void | Promise<void>;
}

export function makeKit(options: KitOptions = {}) {
  evalsMock.access = 'enabled';
  evalsMock.photos = goldenPhotos(options.photos ?? 4);
  // the runner reads shelf photos before single ones, each set in id order
  const ids = [...evalsMock.photos].sort((a, b) => (a.set === b.set ? a.id.localeCompare(b.id) : a.set.localeCompare(b.set))).map((p) => p.id);
  if (options.held) holdRun(options.held);

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
  const realSelector = new EngineSelector({ vision, settings, newId: testIds });
  /** How often the phone was told to stay awake and to let go, and whether it is being held awake right now. */
  const awake = { on: 0, off: 0, held: false };

  /** The app in front or not: `go('background')` and `go('active')` are the owner leaving and coming back. */
  const listeners = new Set<(state: string) => void>();
  const app = {
    state: 'active' as string,
    go(next: 'active' | 'background' | 'inactive') {
      this.state = next;
      listeners.forEach((l) => l(next));
    },
  };
  const appState: AppStateSource = {
    current: () => app.state,
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };

  /** The readings the PC has not taken: shared by every runner this kit makes, so a relaunch finds what the app left. */
  const outbox = new MemoryKeyValueStore();
  /** Every wait the runner asked for, in milliseconds. A wait ends at once. */
  const sleeps: number[] = [];
  const created: EvalRunner[] = [];

  const build = (): EvalRunner => {
    const runner = createEvalRunner({
      api: {
        ...evalApi,
        sendPhoto: async (photoId, results) => {
          await options.onSend?.(photoId);
          return evalApi.sendPhoto(photoId, results);
        },
      },
      files,
      selector: options.selectorThrows
        ? {
            read: async () => {
              throw new Error('the reader broke');
            },
          }
        : realSelector,
      // as on the phone (device.ts): the two lower tiers are the selector's own
      fmText: realSelector.tiers.fmText,
      pipeline: realSelector.tiers.pipeline,
      config: store,
      vision,
      device: { model: 'iPhone16,1', appVersion: '1.2.3' },
      keepAwake: {
        on: async () => {
          awake.on += 1;
          awake.held = true;
        },
        off: async () => {
          awake.off += 1;
          awake.held = false;
        },
      },
      outbox,
      appState,
      sleep: async (ms) => {
        sleeps.push(ms);
        await options.onSleep?.(ms);
      },
    });
    created.push(runner);
    return runner;
  };

  const runner = build();
  return {
    runner,
    vision,
    store,
    ids,
    deleted,
    downloaded,
    awake,
    configFor,
    app,
    sleeps,
    outbox,
    /** The app killed and opened again: a new runner over the same PC, the same outbox and the same phone. */
    relaunch() {
      created.forEach((r) => r.dispose());
      return build();
    },
  };
}

export { failedRead };
