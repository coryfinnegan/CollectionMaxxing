// "Add photos" on the Evals screen: the owner picks photos in the library and each goes, full size, into the PC's golden folder (one request per
// photo, one after another). The owner chooses Cover or Shelf for each and can change the suggested name; the PC says whether the name is free,
// so a name taken since the list was read is offered again as a new suggestion. Nothing is kept on the phone: the picker's copy is read when its turn
// comes and is the system's to clean up. The photos go to the PC's eval folder and nowhere else.
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import type { GoldenPhotoApi, GoldenSet } from './api';
import type { OriginalPicker, OriginalPickResult, OriginalReader } from './originalPicker';

/** The most the PC takes for one photo (EvalsOptions.MaxGoldenPhotoBytes). Checked here too, so a photo that is too big is not sent for nothing. */
export const MAX_PHOTO_BYTES = 40 * 1024 * 1024;
export const MAX_PHOTO_MB = MAX_PHOTO_BYTES / (1024 * 1024);

export const SET_LABEL: Record<GoldenSet, string> = { single: 'Cover', shelf: 'Shelf' };
const NAME_PREFIX: Record<GoldenSet, string> = { single: 'cover', shelf: 'shelf' };

export const DENIED_COPY = 'Photos access is off. Turn it on in Settings to add photos.';
export const PICKER_FAILED_COPY = "Couldn't open your photos. Try again.";

/** Names the PC's file system keeps for itself (the server refuses them too). */
const DEVICE_NAME = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
const NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

export type RowStatus = 'ready' | 'sending' | 'added' | 'taken' | 'too_large' | 'unsupported' | 'failed';

export interface DraftPhoto {
  key: string;
  uri: string;
  set: GoldenSet;
  name: string;
  /** True once the owner typed a name: a suggestion follows the set, a typed name does not. */
  nameEdited: boolean;
  status: RowStatus;
  /** What happened to this photo, when it did not simply go. */
  message: string | null;
}

export interface AddPhotosState {
  phase: 'idle' | 'picking' | 'editing' | 'sending';
  photos: DraftPhoto[];
  /** Why the picker gave nothing (access is off, or it would not open). */
  notice: string | null;
  /** While sending: the photo going now, 1-based, and how many are in this send. */
  position: number;
  batch: number;
}

export interface AddPhotosDeps {
  picker: OriginalPicker;
  read: OriginalReader;
  api: GoldenPhotoApi;
}

const initial = (): AddPhotosState => ({ phase: 'idle', photos: [], notice: null, position: 0, batch: 0 });

/** The first `cover-01`, `cover-02`… (or `shelf-…`) not in `taken` (lower case). */
export function suggestName(set: GoldenSet, taken: ReadonlySet<string>): string {
  for (let n = 1; ; n += 1) {
    const name = `${NAME_PREFIX[set]}-${String(n).padStart(2, '0')}`;
    if (!taken.has(name)) return name;
  }
}

/** Why a name cannot be sent, or null. `taken` holds the lower-case names already in use, the other photos in this list included. */
export function nameProblem(name: string, taken: ReadonlySet<string>): string | null {
  if (name.length === 0) return 'Give it a name.';
  if (!NAME_PATTERN.test(name)) return 'Use letters, numbers, - and _, up to 64 characters.';
  if (DEVICE_NAME.test(name)) return 'Windows keeps that name for itself. Pick another.';
  if (taken.has(name.toLowerCase())) return 'That name is already used. Pick another.';
  return null;
}

const RETRYABLE: readonly RowStatus[] = ['ready', 'taken', 'failed'];
export const isRetryable = (row: DraftPhoto) => RETRYABLE.includes(row.status);

export function photos(count: number): string {
  return `${count} ${count === 1 ? 'photo' : 'photos'}`;
}

export class AddPhotosController {
  private state: AddPhotosState = initial();
  private readonly listeners = new Set<() => void>();
  /** Lower-case names the PC already has (read when photos are picked, and kept up to date as photos go up). */
  private serverNames = new Set<string>();
  private counter = 0;

  constructor(private readonly deps: AddPhotosDeps) {}

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getState = () => this.state;

  /** The problem with each photo's name, by key: only for photos that would be sent. */
  nameProblems(): Record<string, string | null> {
    const result: Record<string, string | null> = {};
    for (const row of this.state.photos) {
      if (!isRetryable(row)) continue;
      result[row.key] = nameProblem(row.name, this.takenExcept(row.key));
    }
    return result;
  }

  /** True when there is something to send and every name can be sent. */
  canSend(): boolean {
    if (this.state.phase === 'sending' || this.state.phase === 'picking') return false;
    const problems = this.nameProblems();
    const keys = Object.keys(problems);
    return keys.length > 0 && keys.every((key) => problems[key] === null);
  }

  /** Opens the photo library for the originals and adds what was picked, each with a suggested name. */
  async pick(): Promise<void> {
    if (this.state.phase === 'sending' || this.state.phase === 'picking') return;
    const before = this.state;
    this.set({ ...before, phase: 'picking', notice: null });
    let result: OriginalPickResult;
    try {
      result = await this.deps.picker.pick();
    } catch {
      this.set({ ...before, notice: PICKER_FAILED_COPY });
      return;
    }
    if (result.status === 'denied') {
      this.set({ ...before, notice: DENIED_COPY });
      return;
    }
    if (result.status === 'cancelled' || result.photos.length === 0) {
      this.set({ ...before, notice: null });
      return;
    }

    await this.learnServerNames();
    // Photos that already went up are done with: only the ones still to send stay on the list, and the new ones join them.
    const kept = before.photos.filter((row) => row.status !== 'added');
    const rows = [...kept];
    for (const picked of result.photos) {
      const taken = new Set([...this.serverNames, ...rows.map((r) => r.name.toLowerCase())]);
      this.counter += 1;
      rows.push({
        key: `photo-${this.counter}`,
        uri: picked.uri,
        set: 'single',
        name: suggestName('single', taken),
        nameEdited: false,
        status: 'ready',
        message: null,
      });
    }
    this.set({ ...before, phase: 'editing', photos: rows, notice: null });
  }

  /** Changes a photo's set. A suggested name follows the set; a name the owner typed stays. */
  setSet(key: string, set: GoldenSet): void {
    this.update(key, (row) => {
      if (row.set === set || !isRetryable(row)) return row;
      const name = row.nameEdited ? row.name : suggestName(set, this.takenExcept(key));
      return { ...row, set, name, status: row.status === 'taken' ? 'ready' : row.status, message: row.status === 'taken' ? null : row.message };
    });
  }

  rename(key: string, name: string): void {
    this.update(key, (row) =>
      isRetryable(row) ? { ...row, name, nameEdited: true, status: row.status === 'taken' ? 'ready' : row.status, message: row.status === 'taken' ? null : row.message } : row,
    );
  }

  remove(key: string): void {
    if (this.state.phase === 'sending') return;
    const rest = this.state.photos.filter((row) => row.key !== key);
    this.set({ ...this.state, photos: rest, phase: rest.length === 0 ? 'idle' : this.state.phase });
  }

  /** Sends the photos that are ready, one after another, and records what happened to each. */
  async send(): Promise<void> {
    if (!this.canSend()) return;
    const queue = this.state.photos.filter(isRetryable).map((row) => row.key);
    this.set({ ...this.state, phase: 'sending', position: 0, batch: queue.length });
    for (const key of queue) {
      this.set({ ...this.state, position: this.state.position + 1 });
      await this.sendOne(key);
    }
    this.set({ ...this.state, phase: 'editing', position: 0, batch: 0 });
  }

  private async sendOne(key: string): Promise<void> {
    const row = this.state.photos.find((r) => r.key === key);
    if (!row) return;
    this.update(key, (r) => ({ ...r, status: 'sending', message: null }));
    try {
      const photo = await this.deps.read(row.uri);
      if (photo.size > MAX_PHOTO_BYTES) {
        this.update(key, (r) => ({ ...r, status: 'too_large', message: `Too large. The limit is ${MAX_PHOTO_MB} MB.` }));
        return;
      }
      await this.deps.api.add(row.set, row.name, photo);
      this.serverNames.add(row.name.toLowerCase());
      this.update(key, (r) => ({ ...r, status: 'added', message: null }));
    } catch (error) {
      this.update(key, (r) => this.afterFailure(r, error));
    }
  }

  private afterFailure(row: DraftPhoto, error: unknown): DraftPhoto {
    if (error instanceof ApiError) {
      if (error.status === 409 && (error.problem.code ?? 'evals.id_taken') === 'evals.id_taken') {
        // Someone (or an earlier run of this list) used the name since it was suggested: the PC's answer wins, and a fresh name is offered.
        this.serverNames.add(row.name.toLowerCase());
        const name = suggestName(row.set, this.takenExcept(row.key));
        return { ...row, name, nameEdited: false, status: 'taken', message: `${row.name} is taken. Try ${name}, or type another name.` };
      }
      if (error.status === 409) {
        return { ...row, status: 'failed', message: "Your PC couldn't save it. Check that the eval folder can be written to, then try again." };
      }
      if (error.status === 413) return { ...row, status: 'too_large', message: `Too large. The limit is ${MAX_PHOTO_MB} MB.` };
      if (error.status === 415) return { ...row, status: 'unsupported', message: "That isn't a JPEG or HEIC photo." };
      if (error.status === 403 || error.status === 404) {
        return { ...row, status: 'failed', message: "Your PC isn't taking eval photos right now. Check that evals are on, then try again." };
      }
    }
    return { ...row, status: 'failed', message: friendlyError(error, "Couldn't send it to your PC. Try again.") };
  }

  private async learnServerNames(): Promise<void> {
    try {
      const listed = await this.deps.api.list();
      this.serverNames = new Set([...this.serverNames, ...listed.map((p) => p.id.toLowerCase())]);
    } catch {
      // The names already known are all there is; the PC answers 409 for a name that is taken.
    }
  }

  /** The lower-case names in use: the PC's, and every other photo on the list. */
  private takenExcept(key: string): Set<string> {
    return new Set([...this.serverNames, ...this.state.photos.filter((r) => r.key !== key).map((r) => r.name.toLowerCase())]);
  }

  private update(key: string, change: (row: DraftPhoto) => DraftPhoto): void {
    this.set({ ...this.state, photos: this.state.photos.map((row) => (row.key === key ? change(row) : row)) });
  }

  private set(next: AddPhotosState): void {
    this.state = next;
    for (const listener of [...this.listeners]) listener();
  }
}
