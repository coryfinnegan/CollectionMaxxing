// A silent, low-rate look at the camera for auto-snap (CAM-3f). VisionCamera v5 has no frame-to-file call and its
// preview `takeSnapshot()` is documented for Android only, so on iOS a still is taken with the shutter sound off,
// scaled to a small JPEG, and the full-size file is deleted at once. The small file is deleted after Vision has read
// it. Nothing here is uploaded and nothing is written outside the temp folder (docs/02 "Privacy").
export const SNAPSHOT_LONG_EDGE = 640;
export const SNAPSHOT_MIN_INTERVAL_MS = 700;

export type StoredOrientation = 'up' | 'right' | 'left' | 'down';

export interface TakenStill {
  uri: string;
  width: number;
  height: number;
  /** How the file is stored; 'right' and 'left' mean the picture is turned a quarter when shown. */
  orientation: StoredOrientation;
}

export interface SilentSnapshotDeps {
  /** One still with no shutter sound and no flash, written to the temp folder. */
  take(): Promise<TakenStill>;
  /** Scales to `size` and saves a small JPEG in the temp folder. */
  shrink(uri: string, size: { width: number; height: number }): Promise<{ uri: string }>;
  remove(uri: string): Promise<void>;
  /** True while the shutter (or auto-snap) is taking its full-size picture. */
  isCapturing(): boolean;
  minIntervalMs?: number;
  now?(): number;
  /** When set, only files it accepts (the temp folders) are ever kept or deleted. */
  isTemp?(uri: string): boolean;
}

/** The size to scale to: long edge 640 as the picture will be seen, never larger than it is. */
export function fitSnapshotSize(width: number, height: number, orientation: StoredOrientation, max = SNAPSHOT_LONG_EDGE) {
  const sideways = orientation === 'right' || orientation === 'left';
  const seenW = sideways ? height : width;
  const seenH = sideways ? width : height;
  const long = Math.max(seenW, seenH);
  // The manipulator turns the picture upright when it loads it, so the target is given as seen.
  const scale = long <= max || long <= 0 ? 1 : max / long;
  const w = Math.max(1, Math.round(seenW * scale));
  const h = Math.max(1, Math.round(seenH * scale));
  return { width: w, height: h };
}

export function createSilentSnapshot(deps: SilentSnapshotDeps) {
  const now = deps.now ?? Date.now;
  const minInterval = deps.minIntervalMs ?? SNAPSHOT_MIN_INTERVAL_MS;
  let lastAt = Number.NEGATIVE_INFINITY;
  let running = false;

  const inTemp = (uri: string) => !deps.isTemp || deps.isTemp(uri);
  const remove = async (uri: string) => {
    if (!inTemp(uri)) return;
    try {
      await deps.remove(uri);
    } catch {
      // the system clears its temp folder anyway
    }
  };

  return {
    /** A small still's uri, or null when it is too soon, busy, or anything fails. */
    async take(): Promise<string | null> {
      if (running || deps.isCapturing() || now() - lastAt < minInterval) return null;
      running = true;
      lastAt = now();
      let full: TakenStill | undefined;
      try {
        full = await deps.take();
        if (!inTemp(full.uri)) return null;
        const small = await deps.shrink(full.uri, fitSnapshotSize(full.width, full.height, full.orientation));
        if (!inTemp(small.uri)) {
          return null;
        }
        return small.uri;
      } catch {
        return null;
      } finally {
        if (full) await remove(full.uri);
        running = false;
      }
    },
    /** Deletes a small still once it has been read. */
    release: remove,
  };
}
