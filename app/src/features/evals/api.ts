// The eval routes (EVAL-1a, and adding golden photos from the phone), owner only. The photo itself is downloaded by `files.ts`, not through this client.
import {
  addGoldenPhoto,
  discardEvalRun,
  finishEvalRun,
  getOpenEvalRun,
  listGoldenPhotos,
  openEvalRun,
  storeEvalPhoto,
} from '@/api/generated/admin/admin';
import type { EvalOpenRunRequest, EvalOpenRunStatus, EvalPhotoResult, EvalPhotoStored, GoldenPhotoDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';

/** The problem codes that say the run the phone was working on is no longer there to take photos. */
export const RUN_GONE_CODES = ['evals.no_open_run', 'evals.run_not_open'] as const;

export interface EvalApi {
  /** Rejects with the server's error (404 evals off, 403 not the owner). */
  list(): Promise<GoldenPhotoDto[]>;
  /** The run the PC holds, with the photos it has, or undefined when there is none. Rejects with the server's error otherwise (404 evals off, 403 not the owner). */
  openRun(): Promise<EvalOpenRunStatus | undefined>;
  /** Opens a run on the PC. Rejects with an `ApiError`: 409 `evals.run_open` when one is already open. */
  startRun(body: EvalOpenRunRequest): Promise<EvalOpenRunStatus>;
  /**
   * Sends one photo's readings (every engine) and resolves once the PC has them stored: it grounds them later, in the background. Sending a photo again
   * replaces it. Rejects with an `ApiError` when the PC refuses the photo (400, which is final) or the run is gone (404/409).
   */
  sendPhoto(photoId: string, results: EvalPhotoResult[]): Promise<EvalPhotoStored>;
  /** Tells the PC to score the run once it has grounded what it holds. `partial: true` is a run stopped early. */
  finishRun(partial: boolean): Promise<EvalOpenRunStatus>;
  /** Throws the open run, and everything the PC stored for it, away. */
  discardRun(): Promise<void>;
}

export const evalApi: EvalApi = {
  async list() {
    const response = await listGoldenPhotos();
    if (response.status !== 200) throw new Error(`The golden list answered ${response.status}.`);
    return response.data;
  },
  async openRun() {
    try {
      const response = await getOpenEvalRun();
      if (response.status !== 200) throw new Error(`The open run answered ${response.status}.`);
      return response.data;
    } catch (error) {
      if (error instanceof ApiError && error.status === 404 && error.problem.code === 'evals.no_open_run') return undefined;
      throw error;
    }
  },
  async startRun(body) {
    const response = await openEvalRun(body);
    if (response.status !== 201) throw new Error(`The run was not opened (${response.status}).`);
    return response.data;
  },
  async sendPhoto(photoId, results) {
    const response = await storeEvalPhoto(photoId, { results });
    if (response.status !== 200) throw new Error(`The photo was not saved (${response.status}).`);
    return response.data;
  },
  async finishRun(partial) {
    const response = await finishEvalRun(partial ? { partial: true } : {});
    if (response.status !== 202) throw new Error(`The run was not finished (${response.status}).`);
    return response.data;
  },
  async discardRun() {
    try {
      await discardEvalRun();
    } catch (error) {
      if (error instanceof ApiError && error.status === 404 && error.problem.code === 'evals.no_open_run') return; // already gone
      throw error;
    }
  },
};

/** The two folders a golden photo can go in: a single item's cover, or a whole shelf. */
export type GoldenSet = 'single' | 'shelf';

/** What "Add photos" needs from the PC: the names already used, and a way to send one original into the golden folder. */
export interface GoldenPhotoApi {
  /** Rejects with the server's error (404 evals off, 403 not the owner). */
  list(): Promise<GoldenPhotoDto[]>;
  /**
   * Sends one photo, as it is, into the PC's golden folder under `<set>/<id>`. Rejects with an `ApiError`: 409 `evals.id_taken` when the name is
   * used in either set, 413 over 40 MB, 415 when it is not a JPEG or a HEIC.
   */
  add(set: GoldenSet, id: string, photo: Blob): Promise<GoldenPhotoDto>;
}

export const goldenPhotoApi: GoldenPhotoApi = {
  list: () => evalApi.list(),
  async add(set, id, photo) {
    // The bytes say what the picture is; the header is only there to say "raw bytes, not JSON".
    const response = await addGoldenPhoto(set, id, photo, { headers: { 'Content-Type': 'application/octet-stream' } });
    if (response.status !== 201) throw new Error(`The photo was not saved (${response.status}).`);
    return response.data;
  },
};
