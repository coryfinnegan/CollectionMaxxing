// The on-phone eval runner (EVAL-1b). It reads the PC's golden photos one at a time through the Apple model (the scanner's own selector), the
// OCR-text extraction tier and the Vision text engine, and sends each photo's readings to the PC the moment they are read. It does no scoring and no
// grounding: the PC does, in the background. The run lives on the PC, not here: it is opened there, the PC says which photos it already has, and the
// phone only ever reads the ones it does not. So a run survives the phone leaving the app, the app being killed, and the PC restarting.
// It never touches the library: the only requests are the eval routes and the config fetch, and a photo file is deleted the moment it has been read.
import type { EvalOpenRunStatus, EvalPhotoResult, EvalReadItem, EvalRunPhoto, ScanTier } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { discardPicture, trackPicture } from '@/features/scan/pictureFiles';
import type { RecognitionSettings } from '@/features/scan/recognition/defaults';
import { toItemBox } from '@/features/scan/recognition/mapping';
import { ModelUnusableError, type EngineName, type OcrReading, type ReadItem, type ReadOptions, type RecognitionEngine, type SnapReading, type Still } from '@/features/scan/recognition/types';
import type { ShotKind } from '@/features/scan/shotkind/shotKind';
import type { KeyValueStore } from '@/features/scan/tray/trayStore';
import type { ThermalState } from '@/native/cm-vision';
import { RUN_GONE_CODES, type EvalApi } from './api';
import { EvalOutbox, type PendingPhoto } from './outbox';

/**
 * Where the runner is.
 * - `checking`: asking the PC whether a run is open. `idle`: none is.
 * - `resumable`: the PC holds a run that is not being read right now (Continue or Start over).
 * - `starting`, `running`: opening a run, reading and sending its photos. `paused`: by the owner, or because the app is in the background.
 * - `waiting`: the PC did not take something; the readings are kept here and go up when it answers.
 * - `finishing`: asking the PC to score. `scoring`: it is scoring; the gate summary shows when it is ready. `done`.
 */
export type EvalPhase = 'checking' | 'idle' | 'resumable' | 'starting' | 'running' | 'paused' | 'waiting' | 'finishing' | 'scoring' | 'done' | 'error';
export type EvalStep = 'apple' | 'text' | 'vision';

export interface EvalState {
  phase: EvalPhase;
  /** Photos in the run. */
  total: number;
  /** Photos read: the PC has them, or they are kept here waiting to go up. */
  photosDone: number;
  /** The photo being read, 1-based; 0 before the first. */
  current: number;
  step: EvalStep | null;
  /** When this stretch of reading began (Start or Continue). */
  startedAt: number | null;
  /** When it stopped reading. */
  endedAt: number | null;
  thermal: ThermalState | undefined;
  /** True once the phone reached serious heat; the run keeps going and records it. */
  keepCool: boolean;
  /** The run was stopped early: the PC scores the photos it has. */
  partial: boolean;
  /** Readings kept on this phone because the PC has not taken them yet. */
  pending: number;
  /** Photos that could not be downloaded, so were not read. */
  skipped: string[];
  /** Photos the PC would not take, so are left out of the score. */
  refused: string[];
  /** Why the run is paused: the owner asked, or the app went to the background. */
  pause?: 'user' | 'background';
  /** The owner tapped Stop, so the run ends early on purpose. */
  stopped: boolean;
  /** Why the PC cannot score the run yet (a problem code); it keeps trying. */
  failure?: string;
  summary?: EvalOpenRunStatus['summary'];
  message?: string;
}

/** The slice of the app's foreground/background state the runner needs. */
export interface AppStateSource {
  current(): string;
  subscribe(listener: (state: string) => void): () => void;
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
  /** Holds the screen awake. The runner holds it only while it is reading in the foreground: never paused, waiting for the PC or while the PC scores. */
  keepAwake: { on(): Promise<void>; off(): Promise<void> };
  /** Where the readings the PC has not taken yet are kept, so an app that is killed loses none of them (MMKV on the phone). */
  outbox: KeyValueStore;
  /** Whether the app is in front. Left out, it always is. */
  appState?: AppStateSource;
  /** Waits this many milliseconds: between sends that failed, and between asks whether the PC has finished scoring. */
  sleep?: (ms: number) => Promise<void>;
  now?: () => number;
  /** Makes the segment id each read stamps its items with; a random one when left out. */
  newId?: () => string;
}

/** `baselineNote` explains a deliberate threshold move (docs/16 §17); empty stays out of the run. */
export interface EvalStartOptions {
  baselineNote?: string;
}

/** How long to wait after a send fails, by how many times in a row it has: 1 s, 2 s, 4 s… up to 30 s. */
export const sendWaitMs = (failures: number): number => Math.min(30_000, 1000 * 2 ** Math.max(0, failures - 1));

/** How often to ask whether the PC has finished scoring, while the screen is open. */
export const SCORING_POLL_MS = 3000;

/** How many server errors in a row the PC may answer one send with before the runner stops and keeps the readings. A PC that does not answer at all is waited for. */
export const MAX_SERVER_REFUSALS = 5;

/** What the owner is told when the PC cannot take what was read. Nothing is thrown away: it is all still on this phone. */
export const PC_NOT_TAKING = "Couldn't send to your PC. What's read is kept on this phone; check again to pick up.";

const initial = (): EvalState => ({
  phase: 'checking',
  total: 0,
  photosDone: 0,
  current: 0,
  step: null,
  startedAt: null,
  endedAt: null,
  thermal: undefined,
  keepCool: false,
  partial: false,
  pending: 0,
  skipped: [],
  refused: [],
  stopped: false,
});

const defaultSegmentId = () =>
  'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/** What the scanner's framing would call a golden photo of this set; a set the runner doesn't know stays unclassified, like a snap stored before classification. */
const shotKindOf = (set: string): ShotKind | undefined => (set === 'single' ? 'single_cover' : set === 'shelf' ? 'shelf_segment' : undefined);

const FELL_BACK = { unavailable: 'unavailable', declined: 'declined', timeout: 'timeout', contextExceeded: 'context' } as const;

const ENGINES: readonly { engine: EngineName; step: EvalStep }[] = [
  { engine: 'apple_model', step: 'apple' },
  { engine: 'fm_text', step: 'text' },
  { engine: 'vision_pipeline', step: 'vision' },
];

const toItem = (item: ReadItem, engine: EngineName, segmentId: string): EvalReadItem => {
  const box = toItemBox(item.box);
  return {
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
    // Where the read sits and the game code it printed, as the scanner sends them: the PC groups reads by place and looks the code up.
    ...(box ? { box } : {}),
    ...(item.productCode ? { productCode: item.productCode } : {}),
    // A read only the picture model made, where Vision found nothing: the PC scores it as review, never an auto-add.
    ...(item.unconfirmed ? { unconfirmed: true } : {}),
    // The phone cannot ground a read, so it claims the commit from confidence: a high-confidence read would
    // auto-commit (T1) and the PC judges whether it was right; anything less is kept as a guess for a human.
    ...(item.confidence === 'high'
      ? { tier: 'T1' as ScanTier, provisional: false }
      : { provisional: true, isGuess: true }),
  };
};

/** What the server accepts of the Vision engine's OCR record: at most this many lines, each with text up to this long. */
export const MAX_OCR_LINES = 1000;
export const MAX_OCR_TEXT = 200;

const unit = (value: number) => Math.min(1, Math.max(0, value));

/**
 * The lines the Vision engine split and the pieces it made of them, shaped for the upload. The PC refuses a photo for a line over its limits, so the
 * record is cut to them here (a box a hair outside the frame is pulled in) instead of losing the photo.
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

/**
 * A photo that could not be downloaded. `status` is the HTTP status the PC answered, when it answered; none means it never did (the network, or a PC that
 * is restarting).
 */
export class DownloadError extends Error {
  constructor(
    message: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = 'DownloadError';
  }
}

/** The PC said the run is not open any more (finished or discarded elsewhere): nothing more can be sent to it. */
class RunGoneError extends Error {}

/** The PC cannot take anything right now (signed out, evals off, results folder not writable, errors that keep coming): the run stops and keeps what it read. */
class PcNotTakingError extends Error {}

const statusOf = (error: unknown): number | undefined => (error instanceof ApiError || error instanceof DownloadError ? error.status : undefined);

/** The PC is there but busy or restarting (the proxy answers 502 while the API redeploys): wait for it, however long it takes. */
const NOT_ANSWERING = new Set([408, 429, 502, 503, 504]);

/** A 4xx other than "too many requests" and "timed out" is the PC's final answer about this request. */
const isFinal = (error: unknown): boolean => {
  const status = statusOf(error);
  return status !== undefined && status >= 400 && status < 500 && !NOT_ANSWERING.has(status);
};

/** The PC answered, and the answer is a plain server error (500...): it counts against a photo, a few times in a row. Not answering does not. */
const isServerError = (error: unknown): boolean => {
  const status = statusOf(error);
  return status !== undefined && status >= 500 && !NOT_ANSWERING.has(status);
};

/** The PC refused the photo itself: it failed its own checks (400) or is too big (413). Nothing else the PC says leaves a photo out. */
const isAboutThePhoto = (error: unknown): error is ApiError => error instanceof ApiError && (error.status === 400 || error.status === 413);

const isRunGone = (error: ApiError) => (RUN_GONE_CODES as readonly string[]).includes(error.problem.code ?? '');

export class EvalRunner {
  private state: EvalState = initial();
  private readonly listeners = new Set<() => void>();
  private readonly now: () => number;
  private readonly newId: () => string;
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly outbox: EvalOutbox;
  private readonly unsubscribeAppState: (() => void) | undefined;

  /** The run the PC holds, as the phone last heard. */
  private run: { runId: string; photos: EvalRunPhoto[]; configVersion: string } | undefined;
  /** Photos the PC holds. */
  private received = new Set<string>();
  /** Photos the PC refused alone, or kept refusing: not read again. */
  private refusedIds = new Set<string>();
  /** Readings read but not yet taken by the PC, in the order they were read. */
  private pending: PendingPhoto[] = [];
  private osVersion = '';
  private watchers = 0;
  private polling = false;

  private active: Promise<void> | undefined;
  private userPause = false;
  private inBackground: boolean;
  private stopRequested = false;
  /** Counts the times the app went to the background. A read that spans a change was cut short by it and is read again, never recorded. */
  private epoch = 0;
  private release: (() => void) | undefined;
  private wake: (() => void) | undefined;
  /** Whether the phone is being held awake right now, so it is turned on once and let go once. */
  private awake = false;
  private awakeWork: Promise<void> = Promise.resolve();

  constructor(private readonly deps: EvalRunnerDeps) {
    this.now = deps.now ?? Date.now;
    this.newId = deps.newId ?? defaultSegmentId;
    this.sleep = deps.sleep ?? defaultSleep;
    this.outbox = new EvalOutbox(deps.outbox);
    this.inBackground = deps.appState?.current() === 'background';
    this.unsubscribeAppState = deps.appState?.subscribe(this.onAppState);
  }

  getState = (): EvalState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** Stops listening to the app's state. For tests and teardown; the runner on the phone lives as long as the app does. */
  dispose(): void {
    this.unsubscribeAppState?.();
  }

  private set(patch: Partial<EvalState>) {
    this.state = { ...this.state, ...patch };
    this.syncAwake();
    this.pollIfNeeded();
    this.listeners.forEach((l) => l());
  }

  /** The screen is open: while it is, the runner asks whether the PC has finished scoring. Call the returned function when the screen closes. */
  watch(): () => void {
    this.watchers += 1;
    this.pollIfNeeded();
    let watching = true;
    return () => {
      if (!watching) return;
      watching = false;
      this.watchers -= 1;
    };
  }

  /** Asks the PC what it holds. Does nothing while a run is being read here: the runner knows more than the PC does. */
  async refresh(): Promise<void> {
    if (this.active) return;
    await this.pull(false);
  }

  /** Looks at the PC again after something went wrong: whatever it holds is offered, and nothing is thrown away. */
  async recheck(): Promise<void> {
    if (this.active) return;
    const before = this.state;
    this.set({ phase: 'checking', message: undefined, skipped: [] });
    await this.pull(false, before.phase === 'error' ? before : undefined);
  }

  /**
   * Asks the PC for its run and takes its word. A run that started meanwhile is not disturbed, unless it is the run itself that is asking. When the
   * PC cannot be asked, `backTo` (the state before) is put back, so an error and the readings it is keeping are not lost to a look that failed.
   */
  private async pull(duringRun: boolean, backTo?: EvalState): Promise<boolean> {
    try {
      const status = await this.deps.api.openRun();
      if (this.active && !duringRun) return true;
      if (status) this.adopt(status);
      else if (this.state.phase === 'checking' || this.state.phase === 'resumable' || this.state.phase === 'scoring') this.becomeIdle();
      return true;
    } catch {
      // The PC may be off, or the evals not switched on: the owner finds out when they start a run.
      if (backTo) this.set({ ...backTo });
      else if ((!this.active || duringRun) && this.state.phase === 'checking') this.becomeIdle();
      return false;
    }
  }

  /** Opens a new run on the PC and reads it. Resolves when the run has been handed to the PC to score, or stopped. One run at a time: a second call joins the first. */
  start(options: EvalStartOptions = {}): Promise<void> {
    if (this.active) return this.active;
    const note = options.baselineNote?.trim();
    return this.launch(() => this.open(note ? note : undefined));
  }

  /** Picks up the run the PC holds where it left off: it reads only the photos the PC does not have. */
  continueRun(): Promise<void> {
    if (this.active) return this.active;
    if (this.state.phase !== 'resumable' || !this.run) return Promise.resolve();
    return this.launch(async () => {
      // Photos skipped earlier are asked for again: whatever kept them from downloading may be over.
      this.set({ phase: 'running', startedAt: this.now(), endedAt: null, message: undefined, skipped: [], stopped: false });
      await this.config();
      await this.loop();
    });
  }

  /** Throws the run the PC holds away, and the readings kept here for it, and goes back to the start. */
  async startOver(): Promise<void> {
    if (this.active) return;
    try {
      await this.deps.api.discardRun();
    } catch {
      this.set({ phase: 'error', message: "Couldn't reach your PC to start over. Try again." });
      return;
    }
    this.outbox.clear();
    this.becomeIdle();
  }

  /** Continues after the owner paused. (A pause because the app went to the background lifts by itself when it comes back.) */
  resume(): void {
    this.userPause = false;
    this.release?.();
  }

  /** Pauses before the next step; the one being read finishes first. */
  pause() {
    if (this.active && this.state.phase === 'running') this.userPause = true;
  }

  /** Stops reading and asks the PC to score the photos it has. Everything read is already there, or is sent first. */
  stop() {
    if (!this.active) return;
    this.stopRequested = true;
    this.set({ stopped: true });
    this.userPause = false;
    this.release?.();
    this.wake?.();
  }

  // ---- the app going to the background and coming back ----------------------------------------------------------------------

  private onAppState = (next: string) => {
    if (next === 'background' && !this.inBackground) {
      this.inBackground = true;
      this.epoch += 1; // whatever is being read now is cut short by this, however it ends
      this.syncAwake();
    } else if (next === 'active' && this.inBackground) {
      this.inBackground = false;
      this.syncAwake();
      this.release?.(); // picks up by itself, unless the owner had paused
      this.wake?.();
      void this.refresh(); // the PC may have finished scoring while the app was away
    }
  };

  // ---- keeping the phone awake only while it reads --------------------------------------------------------------------

  private syncAwake() {
    const wanted = this.state.phase === 'running' && !this.inBackground;
    if (wanted === this.awake) return;
    this.awake = wanted;
    const call = () => (wanted ? this.deps.keepAwake.on() : this.deps.keepAwake.off());
    this.awakeWork = this.awakeWork.then(() => call().catch(() => undefined));
  }

  // ---- opening, continuing, finishing ---------------------------------------------------------------------------------------

  private launch(work: () => Promise<void>): Promise<void> {
    this.stopRequested = false;
    this.userPause = false;
    this.active = work()
      .catch((error: unknown) => {
        if (error instanceof RunGoneError) {
          this.set({ phase: 'error', endedAt: this.now(), message: "The run on your PC isn't open any more. Start a new one." });
        } else if (error instanceof PcNotTakingError) {
          this.set({ phase: 'error', endedAt: this.now(), message: PC_NOT_TAKING });
        } else {
          this.set({ phase: 'error', endedAt: this.now(), message: "Couldn't finish the run. What's read is kept; try again." });
        }
      })
      .finally(async () => {
        this.active = undefined;
        this.release = undefined;
        this.wake = undefined;
        this.syncAwake();
        await this.awakeWork;
      });
    return this.active;
  }

  /** Refreshes the settings. Never throws; the version used is the one this leaves in the store. */
  private async config() {
    await this.deps.config.refresh();
    this.osVersion = (await this.deps.vision.deviceStatus().catch(() => undefined))?.osVersion ?? this.osVersion;
  }

  private async open(baselineNote: string | undefined): Promise<void> {
    this.set({ ...initial(), phase: 'starting', startedAt: this.now() });
    this.run = undefined;
    let status: EvalOpenRunStatus;
    try {
      await this.config();
      status = await this.deps.api.startRun({
        device: { model: this.deps.device.model, osVersion: this.osVersion, appVersion: this.deps.device.appVersion },
        configVersion: this.deps.config.current().version,
        ...(baselineNote ? { baselineNote } : {}),
      });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409 && error.problem.code === 'evals.run_open') {
        if (await this.pull(true)) this.set({ message: 'Your PC is already holding a run. Continue it, or start over.' });
        else this.set({ phase: 'error', message: "Your PC is already holding a run, but it didn't answer when asked about it. Check again." });
        return;
      }
      this.set({ phase: 'error', message: "Couldn't open a run on your PC." });
      return;
    }
    this.adopt(status, 'running');
    await this.loop();
  }

  /** Takes the PC's word for the run: its photos, which it holds, which it refused, and where it is. */
  private adopt(status: EvalOpenRunStatus, whenOpen: EvalPhase = 'resumable') {
    // A photo this phone gave up on (the PC kept answering it with an error) is not on the PC's list, but is still left out of this run.
    const refusedHere = this.run?.runId === status.runId ? [...this.refusedIds] : [];
    this.run = { runId: status.runId, photos: status.photos, configVersion: status.configVersion };
    this.received = new Set(status.received);
    this.refusedIds = new Set([...status.refused.map((r) => r.photoId), ...refusedHere]);
    // Readings kept here that the PC already has were sent before the app stopped; the rest are still to send.
    this.pending = this.outbox.load(status.runId).filter((p) => !this.received.has(p.photoId) && !this.refusedIds.has(p.photoId));
    this.outbox.save(status.runId, this.pending);
    const base: Partial<EvalState> = {
      total: status.photos.length,
      photosDone: this.received.size + this.pending.length,
      pending: this.pending.length,
      refused: [...this.refusedIds],
      partial: status.partial,
      failure: status.failure ?? undefined,
    };
    if (status.state === 'done') {
      this.outbox.clear();
      this.set({ ...base, phase: 'done', summary: status.summary ?? undefined, step: null, current: 0 });
    } else if (status.state === 'finishing') {
      this.set({ ...base, phase: 'scoring', step: null, current: 0 });
    } else {
      this.set({ ...base, phase: whenOpen, step: null, current: 0, summary: undefined });
    }
  }

  private becomeIdle() {
    this.run = undefined;
    this.received = new Set();
    this.refusedIds = new Set();
    this.pending = [];
    this.set({ ...initial(), phase: 'idle', thermal: this.state.thermal });
  }

  // ---- the loop -------------------------------------------------------------------------------------------------------------

  /** The photos the run covers that the PC does not have, did not refuse, and the phone has not already read: in the run's order. */
  private unread(): EvalRunPhoto[] {
    const read = new Set(this.pending.map((p) => p.photoId));
    return (this.run?.photos ?? []).filter((p) => !this.received.has(p.id) && !this.refusedIds.has(p.id) && !read.has(p.id) && !this.state.skipped.includes(p.id));
  }

  private async loop(): Promise<void> {
    this.set({ phase: 'running', partial: false, endedAt: null });
    await this.deliverPending();
    for (let photo = this.unread()[0]; photo && !this.stopRequested; photo = this.unread()[0]) {
      if (!(await this.checkpoint())) break;
      await this.readPhoto(photo);
      await this.deliverPending();
    }
    this.set({ step: null, endedAt: this.now() });
    await this.finish();
  }

  /** Waits here while the run is paused (by the owner, or by the app being in the background). False when the owner stopped it. */
  private async checkpoint(back: EvalPhase = 'running'): Promise<boolean> {
    while ((this.userPause || this.inBackground) && !this.stopRequested) {
      this.set({ phase: 'paused', pause: this.userPause ? 'user' : 'background' });
      await new Promise<void>((resolve) => {
        this.release = resolve;
      });
      this.release = undefined;
    }
    if (this.state.phase === 'paused') this.set({ phase: back, pause: undefined });
    return !this.stopRequested;
  }

  /**
   * Reads one photo with all three engines and keeps its readings to send. If the app went to the background while it was being read, nothing is
   * kept: a read that was cut short must never be recorded as an engine error or a timeout, so the photo is read again from the start.
   */
  private async readPhoto(photo: EvalRunPhoto): Promise<void> {
    const epoch = this.epoch;
    const index = (this.run?.photos ?? []).findIndex((p) => p.id === photo.id);
    this.set({ current: index + 1, step: 'apple' });
    let uri: string;
    try {
      // A PC that is not answering is waited for, as a send is; only a final answer about this photo (it is not there) skips it.
      uri = trackPicture(await this.withPc(() => this.deps.files.download(photo.id), 'running'));
    } catch (error) {
      if (this.epoch !== epoch) return; // the download was cut short by the app leaving: read it again
      // Only an answer about this photo (it is not there) skips it. Signed out, evals off, or server errors that keep coming say nothing about the photo:
      // stop with what is read kept, not photo after photo skipped.
      if (statusOf(error) !== 404 && statusOf(error) !== 410) throw new PcNotTakingError();
      this.set({ skipped: [...this.state.skipped, photo.id] });
      return;
    }
    try {
      const size = await this.deps.files.sizeOf(uri).catch(() => ({ width: 0, height: 0 }));
      const still: Still = { uri, ...size };
      const shotKind = shotKindOf(photo.set);
      const results: EvalPhotoResult[] = [];
      for (const { engine, step } of ENGINES) {
        if (this.stopRequested) return;
        if (!(await this.checkpoint())) return;
        if (this.epoch !== epoch) return;
        this.set({ step });
        results.push(await this.readWith(engine, photo.id, still, shotKind));
        if (this.epoch !== epoch) return;
      }
      this.keep({ photoId: photo.id, results });
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
            // What the cover cue saw on a single cover (rects found and why each was left out, the format it gave, whether it was in time): the PC shows it beside the miss.
            ...(reading.coverCue ? { coverCue: reading.coverCue } : {}),
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

  // ---- sending ---------------------------------------------------------------------------------------------------------------

  /** Keeps a photo's readings on the phone, written down before the first send so an app that is killed mid-send loses nothing. */
  private keep(photo: PendingPhoto) {
    this.pending = [...this.pending.filter((p) => p.photoId !== photo.photoId), photo];
    if (this.run) this.outbox.save(this.run.runId, this.pending);
    this.set({ pending: this.pending.length, photosDone: this.received.size + this.pending.length });
  }

  private settle(photoId: string) {
    this.pending = this.pending.filter((p) => p.photoId !== photoId);
    if (this.run) this.outbox.save(this.run.runId, this.pending);
    this.set({ pending: this.pending.length, photosDone: this.received.size + this.pending.length });
  }

  /**
   * Sends what is kept, oldest first, before anything else is read. A PC that does not answer is waited for, a little longer each time. A photo it
   * refuses because of the photo itself (400, or 413 for one too big) is left out and the run goes on. Anything else final (signed out, evals off, the
   * results folder not writable) or server errors that keep coming mean the PC cannot take anything right now: the run stops with every reading
   * still kept here, for Check again and Continue to send first. Only a refusal about the photo ever throws its readings away.
   */
  private async deliverPending(): Promise<void> {
    while (this.pending.length > 0) {
      const entry = this.pending[0];
      try {
        await this.withPc(() => this.deps.api.sendPhoto(entry.photoId, entry.results), 'running');
        this.received.add(entry.photoId);
      } catch (error) {
        if (error instanceof ApiError && isRunGone(error)) throw new RunGoneError();
        if (!isAboutThePhoto(error)) throw new PcNotTakingError(); // the readings stay in the outbox: nothing is settled
        // The PC will not take this photo: say so, leave it out of the score and go on with the others.
        this.refusedIds.add(entry.photoId);
        this.set({ refused: [...this.refusedIds] });
      }
      this.settle(entry.photoId);
    }
  }

  /**
   * Runs a request to the PC until it answers. A request that never got an answer, or got a busy or restarting answer (408, 429, 502, 503, 504), is
   * tried again after a wait that grows, with no limit (the screen says it is waiting for the PC, and the readings stay kept), and the screen is put
   * back to `back` when it answers. Throws what the PC finally says: a 4xx, or a plain server error that came `MAX_SERVER_REFUSALS` times in a row.
   */
  private async withPc<T>(request: () => Promise<T>, back: EvalPhase): Promise<T> {
    let failures = 0;
    let serverErrors = 0;
    for (;;) {
      try {
        const answer = await request();
        if (this.state.phase === 'waiting') this.set({ phase: back, message: undefined });
        return answer;
      } catch (error) {
        if (isFinal(error)) throw error;
        failures += 1;
        if (isServerError(error)) {
          serverErrors += 1;
          if (serverErrors >= MAX_SERVER_REFUSALS) throw error;
        } else {
          serverErrors = 0; // not answering, or restarting: that is waited for with no limit, and starts the count of plain errors again
        }
        this.set({ phase: 'waiting', message: undefined });
        await this.wait(sendWaitMs(failures));
        await this.holdWhileAway();
      }
    }
  }

  private wait(ms: number): Promise<void> {
    return new Promise<void>((resolve) => {
      const done = () => {
        this.wake = undefined;
        resolve();
      };
      this.wake = done;
      void this.sleep(ms).then(done);
    });
  }

  /** While the app is in the background nothing is sent either. The owner's stop does not wait for it. */
  private async holdWhileAway() {
    while (this.inBackground && !this.stopRequested) {
      await new Promise<void>((resolve) => {
        this.release = resolve;
      });
      this.release = undefined;
    }
  }

  // ---- finishing -------------------------------------------------------------------------------------------------------------

  private async finish(): Promise<void> {
    if (this.received.size === 0) {
      // Nothing the PC could score: leave no empty run behind.
      await this.deps.api.discardRun().catch(() => undefined);
      this.outbox.clear();
      this.becomeIdle();
      this.set({ message: 'Nothing was read, so there was nothing to score.' });
      return;
    }
    this.set({ phase: 'finishing', partial: this.stopRequested });
    let status: EvalOpenRunStatus;
    try {
      status = await this.withPc(() => this.deps.api.finishRun(this.stopRequested), 'finishing');
    } catch (error) {
      if (error instanceof ApiError && isRunGone(error)) throw new RunGoneError();
      this.set({ phase: 'error', message: "Your PC wouldn't score the run. What's read is kept there; try again." });
      return;
    }
    this.adopt(status);
  }

  // ---- watching the PC score ------------------------------------------------------------------------------------------------

  private pollIfNeeded() {
    if (this.polling || this.watchers === 0 || this.state.phase !== 'scoring') return;
    this.polling = true;
    void this.poll().finally(() => {
      this.polling = false;
    });
  }

  private async poll() {
    while (this.watchers > 0 && this.state.phase === 'scoring') {
      await this.sleep(SCORING_POLL_MS);
      if (this.watchers === 0 || this.state.phase !== 'scoring') break;
      try {
        const status = await this.deps.api.openRun();
        if (status && this.state.phase === 'scoring') this.adopt(status);
      } catch {
        // The PC is busy with the score, or restarting: the next ask finds it.
      }
    }
  }
}

export const createEvalRunner = (deps: EvalRunnerDeps): EvalRunner => new EvalRunner(deps);
