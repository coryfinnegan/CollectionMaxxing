// The eval routes (EVAL-1a, and adding golden photos from the phone), owner only. The photo itself is downloaded by `files.ts`, not through this client.
import { addGoldenPhoto, listGoldenPhotos, submitEvalRun } from '@/api/generated/admin/admin';
import type { EvalRunRequest, EvalRunResult, GoldenPhotoDto } from '@/api/generated/models';

export interface EvalApi {
  /** Rejects with the server's error (404 evals off, 403 not the owner). */
  list(): Promise<GoldenPhotoDto[]>;
  /** `partial: true` marks a cancelled run; the server shows it as "N of M photos" and never counts it as a pass or a baseline. */
  submit(body: EvalRunRequest): Promise<EvalRunResult>;
}

export const evalApi: EvalApi = {
  async list() {
    const response = await listGoldenPhotos();
    if (response.status !== 200) throw new Error(`The golden list answered ${response.status}.`);
    return response.data;
  },
  async submit(body) {
    const response = await submitEvalRun(body);
    if (response.status !== 200) throw new Error(`The run was not saved (${response.status}).`);
    return response.data;
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
