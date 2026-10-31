// The real pieces on the phone for the eval runner: photo download into the cache folder, the keep-awake lock, the device name, the outbox in MMKV
// and whether the app is in front.
// Everything native loads on first use; nothing here runs on the web build or in tests (they build their own).
import { AppState } from 'react-native';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';
import { apiBaseUrl } from '@/lib/http';
import { DownloadError, EvalRunner, type AppStateSource, type EvalRunnerDeps } from './runner';
import { evalApi } from './api';

const KEEP_AWAKE_TAG = 'cm-evals';
const FOLDER = 'cm-evals';

/** The HTTP status the PC answered, when the download's error carries one (as a property, or as the number in its message). None means it never answered. */
function statusFrom(error: unknown): number | undefined {
  const { status, statusCode, message } = (error ?? {}) as { status?: unknown; statusCode?: unknown; message?: unknown };
  for (const value of [status, statusCode]) if (typeof value === 'number') return value;
  const found = typeof message === 'string' ? /(?<!\d)([45]\d\d)(?!\d)/.exec(message) : null;
  return found ? Number(found[1]) : undefined;
}

const deviceFiles: EvalRunnerDeps['files'] = {
  async download(id) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const fs = require('expo-file-system') as typeof import('expo-file-system');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { authHeaders } = require('@/lib/auth/session') as typeof import('@/lib/auth/session');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { trackPicture } = require('@/features/scan/pictureFiles') as typeof import('@/features/scan/pictureFiles');
    const dir = new fs.Directory(fs.Paths.cache, FOLDER);
    if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
    const destination = new fs.File(dir, `${id}.jpg`);
    // Registered before the download starts, so a file left by a killed app is found by the next clear.
    trackPicture(destination.uri);
    try {
      const file = await fs.File.downloadFileAsync(`${apiBaseUrl()}/api/admin/evals/golden/${encodeURIComponent(id)}/image`, destination, {
        headers: await authHeaders('GET'),
        idempotent: true,
      });
      return file.uri;
    } catch (error) {
      throw new DownloadError(error instanceof Error ? error.message : 'The photo did not download.', statusFrom(error));
    }
  },
  async sizeOf(uri) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { ImageManipulator } = require('expo-image-manipulator') as typeof import('expo-image-manipulator');
    const image = await ImageManipulator.manipulate(uri).renderAsync();
    return { width: image.width, height: image.height };
  },
};

const deviceKeepAwake: EvalRunnerDeps['keepAwake'] = {
  async on() {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    await (require('expo-keep-awake') as typeof import('expo-keep-awake')).activateKeepAwakeAsync(KEEP_AWAKE_TAG);
  },
  async off() {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    await (require('expo-keep-awake') as typeof import('expo-keep-awake')).deactivateKeepAwake(KEEP_AWAKE_TAG);
  },
};

/** The readings the PC has not taken yet, kept in MMKV so an app that iOS ends mid-run loses none of them. */
function openOutbox(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'eval-outbox' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

/** The app in front or not, as React Native tells it. */
const deviceAppState: AppStateSource = {
  current: () => AppState.currentState,
  subscribe(listener) {
    const subscription = AppState.addEventListener('change', listener);
    return () => subscription.remove();
  },
};

function deviceInfo(): EvalRunnerDeps['device'] {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const Constants = (require('expo-constants') as typeof import('expo-constants')).default;
    const ios = (Constants as unknown as { platform?: { ios?: { model?: string } } }).platform?.ios;
    return { model: ios?.model ?? 'iPhone', appVersion: Constants.expoConfig?.version ?? 'unknown' };
  } catch {
    return { model: 'iPhone', appVersion: 'unknown' };
  }
}

let current: EvalRunner | undefined;

/** The runner over the real engines, the real config store and the real file system, built on first use. */
export function getEvalRunner(): EvalRunner {
  if (!current) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { getRecognition } = require('@/features/scan/recognition/device') as typeof import('@/features/scan/recognition/device');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
    const { store, selector } = getRecognition();
    current = new EvalRunner({
      api: evalApi,
      files: deviceFiles,
      selector,
      // The two lower tiers are the selector's own, cover cue and all: the eval measures what the scanner does.
      fmText: selector.tiers.fmText,
      pipeline: selector.tiers.pipeline,
      config: store,
      vision: visionEngine,
      device: deviceInfo(),
      keepAwake: deviceKeepAwake,
      outbox: openOutbox(),
      appState: deviceAppState,
    });
  }
  return current;
}

export const setEvalRunner = (next: EvalRunner | undefined) => {
  current = next;
};
