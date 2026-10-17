// The real pipeline pieces on the phone: the recognition engines (NAT-2c), the snap store, crops by expo-image-manipulator,
// artwork downloads by expo-file-system and feature prints by cm-vision. Tests build their own pieces; nothing here runs on
// the web build.
import { discardPicture, trackPicture } from '../pictureFiles';
import { getRecognition } from '../recognition/device';
import { getTrayStore } from '../tray/storage';
import type { ArtDeps } from './artStep';
import { restPipelineApi } from './pipelineApi';
import { getPipelineStore } from './pipelineStore';
import { localMatch } from './localMatch';
import { getSnapStore } from './snapStore';
import type { CropMaker, PipelineDeps } from './SnapPipeline';

/** The width of the small picture kept for each item: enough for a receipt line and a feature print. */
export const CROP_WIDTH = 256;

export const deviceCrops: CropMaker = {
  async thumb(still, box) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { ImageManipulator, SaveFormat } = require('expo-image-manipulator') as typeof import('expo-image-manipulator');
    const rendered = await ImageManipulator.manipulate(still.uri)
      .crop({
        originX: Math.round(box.x * still.width),
        originY: Math.round(box.y * still.height),
        width: Math.max(1, Math.round(box.width * still.width)),
        height: Math.max(1, Math.round(box.height * still.height)),
      })
      .resize({ width: CROP_WIDTH })
      .renderAsync();
    const saved = await rendered.saveAsync({ format: SaveFormat.JPEG, compress: 0.7 });
    return saved.uri;
  },
};

function deviceArt(): ArtDeps {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const fs = require('expo-file-system') as typeof import('expo-file-system');
  const folder = () => {
    const dir = new fs.Directory(fs.Paths.cache, 'cm-art');
    if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
    return dir;
  };
  return {
    vision: visionEngine,
    async download({ url, headers }) {
      const file = await fs.File.downloadFileAsync(url, folder(), { headers, idempotent: true });
      return trackPicture(file.uri); // deleted once compared; a clear finds it if the app stops in between
    },
    discard: (uri) => discardPicture(uri),
  };
}

/** Built when the scanner opens. Everything native is loaded on first use. */
export function createDevicePipelineDeps(libraryId: string): PipelineDeps {
  let art: ArtDeps | undefined;
  return {
    libraryId,
    tray: getTrayStore(),
    store: getPipelineStore(),
    snaps: getSnapStore(),
    recognition: { read: (still, onItem) => getRecognition().selector.read(still, onItem) },
    api: restPipelineApi,
    crops: deviceCrops,
    localMatch, // CAM-3e: items confirmed before are sent with their release
    art: {
      get vision() {
        return (art ??= deviceArt()).vision;
      },
      download: (request) => (art ??= deviceArt()).download(request),
      discard: (uri) => (art ??= deviceArt()).discard(uri),
    },
  };
}
