// The local match (docs/02 "Learning loop"): the crop of a new snap is feature-printed on the phone and compared with the prints of
// items the person has confirmed before. A close one answers with that release's id. The answer is a hint: it only fills
// `localMatchReleaseId` on the identify request, the server verifies it, and nothing is accepted or added because of it.
import type { FeaturePrint } from '@/native/cm-vision';
import type { FingerprintStore } from './store';

/** Distance at or under which two prints are the same picture, unless the recognition config says otherwise (`fingerprints.maxDistance`). */
export const DEFAULT_MAX_DISTANCE = 0.35;

export interface PrintVision {
  featurePrint(imageUri: string): Promise<FeaturePrint>;
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
}

/** The threshold from the recognition config when it carries a usable one, else the default. */
export function maxDistanceOf(config: unknown): number {
  const value = (config as { fingerprints?: { maxDistance?: unknown } } | null | undefined)?.fingerprints?.maxDistance;
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : DEFAULT_MAX_DISTANCE;
}

export interface MatcherDeps {
  store: Pick<FingerprintStore, 'all' | 'setDeviceRevision'>;
  vision: PrintVision;
  maxDistance?: () => number;
}

export type LocalMatch = (crop: { uri: string }) => Promise<string | undefined>;

export function createLocalMatch(deps: MatcherDeps): LocalMatch {
  return async (crop) => {
    const stored = deps.store.all();
    if (stored.length === 0) return undefined;
    const mine = await deps.vision.featurePrint(crop.uri);
    deps.store.setDeviceRevision(mine.revision);
    const limit = (deps.maxDistance ?? (() => DEFAULT_MAX_DISTANCE))();
    let best: { releaseId: string; distance: number } | undefined;
    for (const row of stored) {
      if (row.revision !== mine.revision) continue; // a different print format cannot be compared
      const distance = await deps.vision.featurePrintDistance(mine, { revision: row.revision, data: row.print });
      if (Number.isFinite(distance) && distance <= limit && (!best || distance < best.distance)) best = { releaseId: row.releaseId, distance };
    }
    return best?.releaseId;
  };
}
