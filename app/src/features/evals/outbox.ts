// What the eval runner has read but the PC has not yet taken. A photo is read in about 14 seconds and sent the moment it is read; when the send fails
// (the PC is off, the network drops, the app is killed mid-send) its readings are kept here, on the phone, and sent again before the next photo is
// read. They belong to one run: a different run's readings are never sent into another.
import type { EvalPhotoResult } from '@/api/generated/models';
import type { KeyValueStore } from '@/features/scan/tray/trayStore';

const KEY = 'eval-outbox-v1';

/** One photo's readings, all three engines, waiting to be sent. */
export interface PendingPhoto {
  photoId: string;
  results: EvalPhotoResult[];
}

interface Saved {
  runId: string;
  photos: readonly PendingPhoto[];
}

const isPending = (value: unknown): value is PendingPhoto =>
  typeof value === 'object' && value !== null && typeof (value as PendingPhoto).photoId === 'string' && Array.isArray((value as PendingPhoto).results);

export class EvalOutbox {
  constructor(private readonly storage: KeyValueStore) {}

  /** The readings waiting for this run, in the order they were read. Nothing for a run the outbox was not kept for. */
  load(runId: string): PendingPhoto[] {
    try {
      const raw = this.storage.getString(KEY);
      if (!raw) return [];
      const saved = JSON.parse(raw) as Partial<Saved> | null;
      return saved?.runId === runId && Array.isArray(saved.photos) ? saved.photos.filter(isPending) : [];
    } catch {
      return [];
    }
  }

  /** Keeps exactly these readings for this run. Nothing to keep clears the outbox. */
  save(runId: string, photos: readonly PendingPhoto[]): void {
    try {
      if (photos.length === 0) this.storage.remove(KEY);
      else this.storage.set(KEY, JSON.stringify({ runId, photos } satisfies Saved));
    } catch {
      // storage full or unavailable: the readings are still held in memory for as long as the app lives
    }
  }

  clear(): void {
    try {
      this.storage.remove(KEY);
    } catch {
      // nothing to do
    }
  }
}
