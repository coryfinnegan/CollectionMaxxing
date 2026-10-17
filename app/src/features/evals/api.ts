// The three eval routes (EVAL-1a), owner only. The photo itself is downloaded by `files.ts`, not through this client.
import { listGoldenPhotos, submitEvalRun } from '@/api/generated/admin/admin';
import type { EvalRunRequest, EvalRunResult, GoldenPhotoDto } from '@/api/generated/models';

/** What the phone sends: text, fields, timings and ids. `partial` marks a cancelled run. */
export type EvalRunBody = EvalRunRequest & { partial?: boolean };

export interface EvalApi {
  /** Rejects with the server's error (404 evals off, 403 not the owner). */
  list(): Promise<GoldenPhotoDto[]>;
  submit(body: EvalRunBody): Promise<EvalRunResult>;
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
