// The real pieces on the phone for the eval runner: photo download into the cache folder, the keep-awake lock, the device name.
// Everything native loads on first use; nothing here runs on the web build or in tests (they build their own).
import { apiBaseUrl } from '@/lib/http';
import { EvalRunner, type EvalRunnerDeps } from './runner';
import { evalApi } from './api';

const KEEP_AWAKE_TAG = 'cm-evals';
const FOLDER = 'cm-evals';

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
    const file = await fs.File.downloadFileAsync(`${apiBaseUrl()}/api/admin/evals/golden/${encodeURIComponent(id)}/image`, destination, {
      headers: await authHeaders('GET'),
      idempotent: true,
    });
    return file.uri;
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
    const { VisionPipelineEngine } = require('@/features/scan/recognition/VisionPipelineEngine') as typeof import('@/features/scan/recognition/VisionPipelineEngine');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { FmTextEngine } = require('@/features/scan/recognition/FmTextEngine') as typeof import('@/features/scan/recognition/FmTextEngine');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
    const { store, selector } = getRecognition();
    current = new EvalRunner({
      api: evalApi,
      files: deviceFiles,
      selector,
      fmText: new FmTextEngine({ vision: visionEngine, settings: () => store.current() }),
      pipeline: new VisionPipelineEngine({ vision: visionEngine, settings: () => store.current() }),
      config: store,
      vision: visionEngine,
      device: deviceInfo(),
      keepAwake: deviceKeepAwake,
    });
  }
  return current;
}

export const setEvalRunner = (next: EvalRunner | undefined) => {
  current = next;
};
