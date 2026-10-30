// The on-phone eval runner (EVAL-1b). It downloads the golden photos one at a time, reads each through the Apple model (the scanner's own
// selector), through the OCR-text extraction tier and through the Vision text engine, and uploads the readings. It does no scoring and no
// grounding: the PC does. It never touches the library: the only requests are the eval routes and the config fetch, and a photo file is
// deleted the moment it has been read.
import type { EvalPhotoResult, EvalReadItem, EvalRunRequest, EvalRunResult, GoldenPhotoDto, ScanTier } from '@/api/generated/models';
import { discardPicture, trackPicture } from '@/features/scan/pictureFiles';
import type { RecognitionSettings } from '@/features/scan/recognition/defaults';
import { ModelUnusableError, type EngineName, type OcrReading, type ReadItem, type ReadOptions, type RecognitionEngine, type SnapReading, type Still } from '@/features/scan/recognition/types';
import type { ShotKind } from '@/features/scan/shotkind/shotKind';
import type { ThermalState } from '@/native/cm-vision';
import type { EvalApi } from './api';

/** A run body stays well under the server's 2 MB limit; a bigger run goes up in several. */
export const MAX_BODY_CHARS = 1_500_000;

export type EvalPhase = 'idle' | 'starting' | 'running' | 'paused' | 'uploading' | 'done' | 'upload_failed' | 'error';
export type EvalStep = 'apple' | 'text' | 'vision';

export interface EvalState {
  phase: EvalPhase;
  total: number;
  /** Photos fully read (all three engines). */
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
  /** The scanner's own selector: the Apple model, then the tiers below it when the model gives no reading. */
  selector: { read(still: Still, onItem: (item: ReadItem) => void, options?: ReadOptions): Promise<SnapReading> };
  /** The OCR-text extraction tier on its own, forced. */
  fmText: RecognitionEngine;
  /** The Vision text engine on its own, forced. */
  pipeline: RecognitionEngine;
  config: { refresh(): Promise<unknown>; current(): RecognitionSettings };
  vision: { deviceStatus(): Promise<{ thermalState: ThermalState; osVersion: string }> };
  device: { model: string; appVersion: string };
  keepAwake: { on(): Promise<void>; off(): Promise<void> };
  now?: () => number;
  /** Makes the segment id each read stamps its items with; a random one when left out. */
  newId?: () => string;
}

/** `baselineNote` explains a deliberate threshold move (docs/16 §17); empty stays out of the upload. */
export interface EvalStartOptions {
  baselineNote?: string;
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

const defaultSegmentId = () =>
  'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });

/** What the scanner's framing would call a golden photo of this set; a set the runner doesn't know stays unclassified, like a snap stored before classification. */
const shotKindOf = (set: string): ShotKind | undefined => (set === 'single' ? 'single_cover' : set === 'shelf' ? 'shelf_segment' : undefined);

const FELL_BACK = { unavailable: 'unavailable', declined: 'declined', timeout: 'timeout', contextExceeded: 'context' } as const;

const toItem = (item: ReadItem, engine: EngineName, segmentId: string): EvalReadItem => ({
  localId: item.localId,
  text: item.text,
  title: item.title,
  creator: item.creator,
  edition: item.edition,
  completeness: item.completeness,
  kind: item.kind,
  format: item.format,
  platform: item.platform,
  resolvedBy: engine === 'vision_pipeline' ? 'vision_text' : 'model',
  segmentId,
  // The phone cannot ground a read, so it claims the commit from confidence: a high-confidence read would
  // auto-commit (T1) and the PC judges whether it was right; anything less is kept as a guess for a human.
  ...(item.confidence === 'high'
    ? { tier: 'T1' as ScanTier, provisional: false }
    : { provisional: true, isGuess: true }),
});

/** What the server accepts of the Vision engine's OCR record: at most this many lines, each with text up to this long. */
export const MAX_OCR_LINES = 1000;
export const MAX_OCR_TEXT = 200;

const unit = (value: number) => Math.min(1, Math.max(0, value));

/**
 * The lines the Vision engine split and the pieces it made of them, shaped for the upload. The server refuses a whole run for a line
 * over its limits, so the record is cut to them here (a box a hair outside the frame is pulled in) instead of losing the run.
 */
export function ocrRecord(ocr: OcrReading | undefined): Pick<EvalPhotoResult, 'ocrLines' | 'ocrPieces'> {
  if (!ocr) return {};
  const lines = ocr.lines.slice(0, MAX_OCR_LINES);
  return {
    ocrLines: lines.map((l) => ({
      text: l.text.slice(0, MAX_OCR_TEXT),
      x: unit(l.box.x),
      y: unit(l.box.y),
      width: unit(l.box.width),
      height: unit(l.box.height),
    })),
    ocrPieces: ocr.pieces.map((piece) => piece.filter((index) => index >= 0 && index < lines.length)).filter((piece) => piece.length > 0),
  };
}

export class EvalRunner {
  private state: EvalState = initial();
  private readonly listeners = new Set<() => void>();
  private readonly now: () => number;
  private readonly newId: () => string;

  private photos: GoldenPhotoDto[] = [];
  private results: EvalPhotoResult[] = [];
  private next = 0;
  private configVersion = '';
  private osVersion = '';
  private baselineNote?: string;
  private uploadedChunks = 0;
  private cancelRequested = false;
  private pauseRequested = false;
  /** Whether the phone is being held awake right now, so it is turned on once and let go once. */
  private awake = false;
  private release: (() => void) | undefined;
  private active: Promise<void> | undefined;
  /** The photo whose first steps are done but the photo unfinished, kept across a pause. */
  private inFlight: EvalPhotoResult[] = [];

  constructor(private readonly deps: EvalRunnerDeps) {
    this.now = deps.now ?? Date.now;
    this.newId = deps.newId ?? defaultSegmentId;
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
  start(options: EvalStartOptions = {}): Promise<void> {
    if (this.active) return this.active;
    this.set(initial());
    this.photos = [];
    this.results = [];
    this.inFlight = [];
    this.next = 0;
    this.baselineNote = options.baselineNote?.trim() ? options.baselineNote.trim() : undefined;
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
    this.active = this.sendAwake().finally(() => {
      this.active = undefined;
    });
    return this.active;
  }

  /** Keeps the phone awake for as long as the run is held; a second call while it is already held does nothing. */
  private async holdAwake(): Promise<void> {
    if (this.awake) return;
    this.awake = true;
    await this.deps.keepAwake.on().catch(() => undefined);
  }

  /** Lets the phone sleep again. Does nothing when it is not being held. */
  private async letGo(): Promise<void> {
    if (!this.awake) return;
    this.awake = false;
    await this.deps.keepAwake.off().catch(() => undefined);
  }

  /** Sends the results with the phone held awake, so a long send is not cut off by the auto-lock. */
  private async sendAwake(): Promise<void> {
    await this.holdAwake();
    try {
      await this.upload();
    } finally {
      await this.letGo();
    }
  }

  private launch(fresh: boolean): Promise<void> {
    this.cancelRequested = false;
    this.pauseRequested = false;
    this.active = this.execute(fresh).finally(() => {
      this.active = undefined;
    });
    return this.active;
  }

  /** The phone stays awake from the start of the run until the results are sent (or the run ended without any), not only while reading. */
  private async execute(fresh: boolean): Promise<void> {
    await this.holdAwake();
    try {
      await this.run(fresh);
    } finally {
      await this.letGo();
    }
  }

  private async run(fresh: boolean): Promise<void> {
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
    while (this.next < this.photos.length) {
      if (!(await this.checkpoint())) break;
      if (!(await this.readPhoto(this.photos[this.next]))) break;
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

  /** Reads one photo with all three engines. False when cancelled before it was finished (it is read again on resume). */
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
      const shotKind = shotKindOf(photo.set);
      if (this.inFlight.length === 0) this.inFlight = [await this.readWith('apple_model', photo.id, still, shotKind)];
      if (!(await this.checkpoint())) return false;
      this.set({ step: 'text' });
      if (this.inFlight.length === 1) this.inFlight.push(await this.readWith('fm_text', photo.id, still, shotKind));
      if (!(await this.checkpoint())) return false;
      this.set({ step: 'vision' });
      const vision = await this.readWith('vision_pipeline', photo.id, still, shotKind);
      this.results.push(...this.inFlight, vision);
      this.inFlight = [];
      this.next += 1;
      this.set({ photosDone: this.state.photosDone + 1, resultCount: this.results.length });
      return true;
    } finally {
      await discardPicture(uri);
    }
  }

  private async readWith(engine: EngineName, photoId: string, still: Still, shotKind?: ShotKind): Promise<EvalPhotoResult> {
    const started = this.now();
    // Every engine is told what the scanner's framing would have told it, so a single is read as a cover and a shelf as a shelf.
    const options: ReadOptions = shotKind ? { shotKind } : {};
    const segmentId = this.newId();
    const capturedAt = new Date(started).toISOString();
    let firstItemMs: number | undefined;
    const onItem = () => {
      firstItemMs ??= this.now() - started;
    };
    let result: EvalPhotoResult;
    try {
      const reading =
        engine === 'apple_model'
          ? await this.deps.selector.read(still, onItem, { ...options, combine: false }) // the Vision engine is read and scored on its own; the PC builds the combination
          : engine === 'fm_text'
            ? await this.deps.fmText.read(still, onItem, options)
            : await this.deps.pipeline.read(still, onItem, options);
      const modelFailed = engine === 'apple_model' && reading.engine !== 'apple_model';
      const reason = reading.fellBackFrom;
      result = modelFailed
        ? {
            photoId,
            engine,
            outcome: reason === 'declined' ? 'refused' : reason === 'timeout' ? 'timeout' : 'error',
            fellBackFrom: FELL_BACK[reason ?? 'unavailable'],
            items: [], // what a fallback tier read is the fm_text and Vision rows' to report
            unreadableCount: 0,
          }
        : {
            photoId,
            engine,
            outcome: 'ok',
            items: reading.items.map((i) => toItem(i, engine, segmentId)),
            unreadableCount: reading.unreadableCount,
            firstItemMs,
            ...(engine === 'vision_pipeline' ? ocrRecord(reading.ocr) : {}),
          };
    } catch (error) {
      // The forced extraction tier reports why it gave no reading, like the Apple row does.
      const reason = engine === 'fm_text' && error instanceof ModelUnusableError ? error.reason : undefined;
      result =
        reason === undefined
          ? { photoId, engine, outcome: 'error', items: [], unreadableCount: 0 }
          : {
              photoId,
              engine,
              outcome: reason === 'declined' ? 'refused' : reason === 'timeout' ? 'timeout' : 'error',
              fellBackFrom: FELL_BACK[reason],
              items: [],
              unreadableCount: 0,
            };
    }
    const thermal = (await this.deps.vision.deviceStatus().catch(() => undefined))?.thermalState;
    if (thermal) {
      this.set({ thermal, keepCool: this.state.keepCool || thermal === 'serious' || thermal === 'critical' });
    }
    // One read is one captured segment, shared by its items; reads the engine refused or failed captured none.
    // Duplicate evidence is never sent: the runner never touches the library, so it has none.
    const capturedCount = (result.unreadableCount ?? 0) + (result.items?.length ? 1 : 0);
    return { ...result, latencyMs: Math.round(this.now() - started), thermalState: thermal, capturedCount, capturedAt };
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
          ...(this.baselineNote ? { baselineNote: this.baselineNote } : {}),
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
