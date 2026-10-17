// The real pieces on the phone: the vision module's feature prints, the threshold from the recognition config, and the cover fetch used to
// recompute a print of another revision. Everything native loads on first use; nothing here runs on the web build.
import { discardPicture, trackPicture } from '../pictureFiles';
import { maxDistanceOf, type PrintVision } from './matcher';

export const deviceVision = (): PrintVision => ({
  featurePrint(uri) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
    return visionEngine.featurePrint(uri);
  },
  featurePrintDistance(a, b) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
    return visionEngine.featurePrintDistance(a, b);
  },
});

/** The distance threshold from the cached recognition config (a built-in default when it carries none). */
export function deviceMaxDistance(): number {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { getRecognition } = require('../recognition/device') as typeof import('../recognition/device');
    return maxDistanceOf(getRecognition().store.current());
  } catch {
    return maxDistanceOf(undefined);
  }
}

export type CoverSource = (releaseId: string) => Promise<string | undefined>;

/**
 * Fetches a release's cover from this server's own artwork route into the cache folder. The file is registered with the picture register
 * the moment it exists, so a sign-out finds it even if the app stops before it is released. Returns undefined when anything fails.
 */
export const deviceCover: CoverSource = async (releaseId) => {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { listReleaseArtwork } = require('@/api/generated/catalog/catalog') as typeof import('@/api/generated/catalog/catalog');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { ownArtworkUrl } = require('../pipeline/artStep') as typeof import('../pipeline/artStep');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { authHeaders } = require('@/lib/auth/session') as typeof import('@/lib/auth/session');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const fs = require('expo-file-system') as typeof import('expo-file-system');
    const response = await listReleaseArtwork(releaseId);
    if (response.status !== 200) return undefined;
    const art = response.data.items.find((a) => a.kind === 'cover') ?? response.data.items[0];
    const url = art ? ownArtworkUrl(art.url) : undefined;
    if (!url) return undefined;
    const dir = new fs.Directory(fs.Paths.cache, 'cm-fingerprint-covers');
    if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
    const file = await fs.File.downloadFileAsync(url, dir, { headers: await authHeaders('GET'), idempotent: true });
    return trackPicture(file.uri);
  } catch {
    return undefined;
  }
};

export const discardCover = (uri: string) => discardPicture(uri);
