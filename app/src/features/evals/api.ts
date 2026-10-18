// The three eval routes (EVAL-1a), owner only. The photo itself is downloaded by `files.ts`, not through this client.
import { listGoldenPhotos, submitEvalRun } from '@/api/generated/admin/admin';
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
