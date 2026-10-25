// The on-phone eval runner (EVAL-1b). It downloads the golden photos one at a time, reads each through the Apple model (the scanner's own
// selector) and through the Vision text engine, and uploads the readings. It does no scoring and no grounding: the PC does. It never touches
// the library: the only requests are the eval routes and the config fetch, and a photo file is deleted the moment it has been read.
import type { EvalPhotoResult, EvalReadItem, EvalRunRequest, EvalRunResult, GoldenPhotoDto } from '@/api/generated/models';
import { discardPicture, trackPicture } from '@/features/scan/pictureFiles';
import type { RecognitionSettings } from '@/features/scan/recognition/defaults';
import type { ReadItem, RecognitionEngine, SnapReading, Still } from '@/features/scan/recognition/types';
import type { ThermalState } from '@/native/cm-vision';
import type { EvalApi } from './api';

/** A run body stays well under the server's 2 MB limit; a bigger run goes up in several. */
export const MAX_BODY_CHARS = 1_500_000;

export type EvalPhase = 'idle' | 'starting' | 'running' | 'paused' | 'uploading' | 'done' | 'upload_failed' | 'error';
export type EvalStep = 'apple' | 'vision';

export interface EvalState {
  phase: EvalPhase;
  total: number;
  /** Photos fully read (both engines). */
  photosDone: number;
  /** The photo being read, 1-based; 0 before the first. */
  current: number;
  step: EvalStep | null;
  startedAt: number | null;
  /** When the last photo was read (or the run was cancelled). */
  endedAt: number | null;
  thermal: ThermalState | undefined;
  /** True once the phone reached serious heat; the run keeps going and records it. */
  keepCool: boolean;
  /** The run was cancelled: what went up is the photos read so far. */
  partial: boolean;
  resultCount: number;
  /** Photos that could not be downloaded, so were not read. */
  skipped: string[];
  summary?: EvalRunResult['summary'];
  message?: string;
}

export interface EvalRunnerDeps {
  api: EvalApi;
  files: {
    /** Downloads one golden photo to a temp file and returns its address. The file is deleted by the runner. */
    download(id: string): Promise<string>;
    sizeOf(uri: string): Promise<{ width: number; height: number }>;
  };
  /** The scanner's own selector: the Apple model, and the Vision text engine when the model gives no reading. */
  selector: { read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> };
  /** The Vision text engine on its own, forced. */
  pipeline: RecognitionEngine;
  config: { refresh(): Promise<unknown>; current(): RecognitionSettings };
  vision: { deviceStatus(): Promise<{ thermalState: ThermalState; osVersion: string }> };
  device: { model: string; appVersion: string };
  keepAwake: { on(): Promise<void>; off(): Promise<void> };
  now?: () => number;
}

const initial = (): EvalState => ({
  phase: 'idle',
  total: 0,
  photosDone: 0,
  current: 0,
  step: null,
  startedAt: null,
  endedAt: null,
  thermal: undefined,
  keepCool: false,
  partial: false,
  resultCount: 0,
  skipped: [],
});

const byOrder = (a: GoldenPhotoDto, b: GoldenPhotoDto) => (a.set === b.set ? (a.id < b.id ? -1 : a.id > b.id ? 1 : 0) : a.set < b.set ? -1 : 1);

const FELL_BACK = { unavailable: 'unavailable', declined: 'declined', timeout: 'timeout', contextExceeded: 'context' } as const;

const toItem = (item: ReadItem, engine: 'apple_model' | 'vision_pipeline'): EvalReadItem => ({
  localId: item.localId,
  text: item.text,
  title: item.title,
  creator: item.creator,
  edition: item.edition,
  completeness: item.completeness,
  kind: item.kind,
  format: item.format,
  platform: item.platform,
  resolvedBy: engine === 'apple_model' ? 'model' : 'vision_text',
});

export class EvalRunner {
  private state: EvalState = initial();
  private readonly listeners = new Set<() => void>();
  private readonly now: () => number;

  private photos: GoldenPhotoDto[] = [];
  private results: EvalPhotoResult[] = [];
  private next = 0;
  private configVersion = '';
  private osVersion = '';
  private uploadedChunks = 0;
  private cancelRequested = false;
  private pauseRequested = false;
  private release: (() => void) | undefined;
  private active: Promise<void> | undefined;
  /** The photo whose first step is done but not its second, kept across a pause. */
  private inFlight: EvalPhotoResult[] = [];

  constructor(private readonly deps: EvalRunnerDeps) {
    this.now = deps.now ?? Date.now;
  }

  getState = (): EvalState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private set(patch: Partial<EvalState>) {
    this.state = { ...this.state, ...patch };
    this.listeners.forEach((l) => l());
  }

  /** Starts a new run. Resolves when it has been uploaded, failed, or been cancelled. One run at a time: a second call joins the first. */
  start(): Promise<void> {
    if (this.active) return this.active;
    this.set(initial());
    this.photos = [];
    this.results = [];
    this.inFlight = [];
    this.next = 0;
    this.uploadedChunks = 0;
    return this.launch(true);
  }

  /** Continues a paused run, or one that was cancelled, after the last photo it read. */
  resume(): Promise<void> {
    if (this.active) {
      this.pauseRequested = false;
      this.release?.();
      return this.active;
    }
    if (this.state.phase !== 'done' || !this.state.partial) return Promise.resolve();
    return this.launch(false);
  }

  /** Pauses before the next step; the one being read finishes first. */
  pause() {
    if (this.active && this.state.phase === 'running') this.pauseRequested = true;
  }

  /** Stops before the next step and uploads the photos read so far, flagged partial. */
  cancel() {
    if (!this.active) return;
    this.cancelRequested = true;
    this.pauseRequested = false;
    this.release?.();
  }

  /** Sends the results again after a failed upload. */
  retryUpload(): Promise<void> {
    if (this.active || this.state.phase !== 'upload_failed') return Promise.resolve();
    this.active = this.upload().finally(() => {
      this.active = undefined;
    });
    return this.active;
  }

  private launch(fresh: boolean): Promise<void> {
    this.cancelRequested = false;
    this.pauseRequested = false;
    this.active = this.execute(fresh).finally(() => {
      this.active = undefined;
    });
    return this.active;
  }

  private async execute(fresh: boolean): Promise<void> {
    if (fresh) {
      this.set({ phase: 'starting', startedAt: this.now() });
      try {
        await this.deps.config.refresh(); // never throws; the version used is the one this leaves in the store
        this.configVersion = this.deps.config.current().version;
        const listed = await this.deps.api.list();
        this.photos = [...listed].sort(byOrder);
        this.osVersion = (await this.deps.vision.deviceStatus().catch(() => undefined))?.osVersion ?? '';
      } catch {
        this.set({ phase: 'error', message: "Couldn't get the photo list from your PC." });
        return;
      }
      this.set({ total: this.photos.length });
    }
    this.set({ phase: 'running', partial: false, endedAt: null });
    await this.deps.keepAwake.on().catch(() => undefined);
    try {
      while (this.next < this.photos.length) {
        if (!(await this.checkpoint())) break;
        if (!(await this.readPhoto(this.photos[this.next]))) break;
      }
    } finally {
      await this.deps.keepAwake.off().catch(() => undefined);
    }
    const cancelled = this.cancelRequested;
    this.set({ partial: cancelled && this.next < this.photos.length, step: null, endedAt: this.now() });
    if (this.results.length === 0) {
      this.set({ phase: 'done', message: cancelled ? 'Canceled before anything was read.' : 'There were no photos to read.' });
      return;
    }
    this.uploadedChunks = 0;
    await this.upload();
  }

  /** False when the run was cancelled. Waits here while paused. */
  private async checkpoint(): Promise<boolean> {
    if (this.pauseRequested && !this.cancelRequested) {
      this.set({ phase: 'paused' });
      await new Promise<void>((resolve) => {
        this.release = resolve;
      });
      this.release = undefined;
      if (!this.cancelRequested) this.set({ phase: 'running' });
    }
    return !this.cancelRequested;
  }

  /** Reads one photo with both engines. False when cancelled before it was finished (it is read again on resume). */
  private async readPhoto(photo: GoldenPhotoDto): Promise<boolean> {
    let uri: string;
    this.set({ current: this.next + 1, step: 'apple' });
    try {
      uri = trackPicture(await this.deps.files.download(photo.id));
    } catch {
      this.set({ skipped: [...this.state.skipped, photo.id], photosDone: this.state.photosDone + 1 });
      this.next += 1;
      return true;
    }
    try {
      const size = await this.deps.files.sizeOf(uri).catch(() => ({ width: 0, height: 0 }));
      const still: Still = { uri, ...size };
      if (this.inFlight.length === 0) this.inFlight = [await this.readWith('apple_model', photo.id, still)];
      if (!(await this.checkpoint())) return false;
      this.set({ step: 'vision' });
      const vision = await this.readWith('vision_pipeline', photo.id, still);
      this.results.push(...this.inFlight, vision);
      this.inFlight = [];
      this.next += 1;
      this.set({ photosDone: this.state.photosDone + 1, resultCount: this.results.length });
      return true;
    } finally {
      await discardPicture(uri);
    }
  }

  private async readWith(engine: 'apple_model' | 'vision_pipeline', photoId: string, still: Still): Promise<EvalPhotoResult> {
    const started = this.now();
    let firstItemMs: number | undefined;
    const onItem = () => {
      firstItemMs ??= this.now() - started;
    };
    let result: EvalPhotoResult;
    try {
      const reading = engine === 'apple_model' ? await this.deps.selector.read(still, onItem) : await this.deps.pipeline.read(still, onItem);
      const modelFailed = engine === 'apple_model' && reading.engine !== 'apple_model';
      const reason = reading.fellBackFrom;
      result = modelFailed
        ? {
            photoId,
            engine,
            outcome: reason === 'declined' ? 'refused' : reason === 'timeout' ? 'timeout' : 'error',
            fellBackFrom: FELL_BACK[reason ?? 'unavailable'],
            items: [], // what the fallback read is the Vision row's to report
            unreadableCount: 0,
          }
        : { photoId, engine, outcome: 'ok', items: reading.items.map((i) => toItem(i, engine)), unreadableCount: reading.unreadableCount, firstItemMs };
    } catch {
      result = { photoId, engine, outcome: 'error', items: [], unreadableCount: 0 };
    }
    const thermal = (await this.deps.vision.deviceStatus().catch(() => undefined))?.thermalState;
    if (thermal) {
      this.set({ thermal, keepCool: this.state.keepCool || thermal === 'serious' || thermal === 'critical' });
    }
    return { ...result, latencyMs: Math.round(this.now() - started), thermalState: thermal };
  }

  /** Photos stay together, and a body stays under the size limit. */
  private chunks(): EvalPhotoResult[][] {
    const chunks: EvalPhotoResult[][] = [];
    let current: EvalPhotoResult[] = [];
    let size = 0;
    for (const result of this.results) {
      const length = JSON.stringify(result).length;
      if (current.length > 0 && size + length > MAX_BODY_CHARS && result.engine === 'apple_model') {
        chunks.push(current);
        current = [];
        size = 0;
      }
      current.push(result);
      size += length;
    }
    if (current.length > 0) chunks.push(current);
    return chunks;
  }

  private async upload(): Promise<void> {
    this.set({ phase: 'uploading', message: undefined });
    const chunks = this.chunks();
    const partial = this.state.partial;
    try {
      let last: EvalRunResult | undefined;
      for (let i = this.uploadedChunks; i < chunks.length; i += 1) {
        const body: EvalRunRequest = {
          device: { model: this.deps.device.model, osVersion: this.osVersion, appVersion: this.deps.device.appVersion },
          configVersion: this.configVersion,
          results: chunks[i],
          ...(partial ? { partial: true } : {}),
        };
        last = await this.deps.api.submit(body);
        this.uploadedChunks = i + 1;
      }
      this.set({ phase: 'done', summary: last?.summary ?? this.state.summary });
    } catch {
      this.set({ phase: 'upload_failed', message: "Your PC didn't take the results. They're still here; try again." });
    }
  }
}

export const createEvalRunner = (deps: EvalRunnerDeps): EvalRunner => new EvalRunner(deps);
