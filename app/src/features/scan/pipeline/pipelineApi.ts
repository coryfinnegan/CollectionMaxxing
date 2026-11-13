// The pipeline's calls to the API, through the generated client. Identify and :art are JSON; :verify-cover posts the
// cover capture (multipart), the one picture that leaves the phone from the pipeline.
import { identifyItems, readShelfSnap, scoreCandidateArtwork, verifyCoverArtwork } from '@/api/generated/scanning/scanning';
import type { CandidateDto, CreateScanSessionRequest, IdentifyRequest } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { createSessionOnce } from '../session/sessionApi';

export interface ArtDistance {
  releaseId: string;
  distance: number;
}

export interface PipelineApi {
  /** Makes the library's scan session, with the context (location, status, media, channel) Review inherits. */
  createSession(libraryId: string, body?: CreateScanSessionRequest): Promise<string>;
  /** One call per snap. The server answers every item it could settle at once and streams the rest. */
  identify(sessionId: string, request: IdentifyRequest): Promise<CandidateDto[]>;
  /** The artwork distances for one close call. Resolves to the re-scored candidate, or undefined when the server leaves it as it is (409). */
  scoreArt(sessionId: string, candidateId: string, distances: ArtDistance[]): Promise<CandidateDto | undefined>;
  /** The cover capture for one ambiguous close call. Resolves to the re-ranked candidate, or undefined when the server refuses it (409). */
  verifyCover(sessionId: string, candidateId: string, file: Blob, quad?: string): Promise<CandidateDto | undefined>;
  /**
   * A shelf snap's still (a JPEG) for the server's own read (#809). Resolves to the rows the server added followed by the phone's rows
   * it skipped. Throws an ApiError for every refusal; the caller decides which are worth another try. `signal` aborts the upload.
   */
  readShelf(sessionId: string, snapId: string, file: Blob, signal?: AbortSignal): Promise<CandidateDto[]>;
}

export const restPipelineApi: PipelineApi = {
  createSession: createSessionOnce,
  async identify(sessionId, request) {
    const response = await identifyItems(sessionId, request);
    if (response.status !== 200) throw new ApiError(response.status, {});
    return response.data.candidates;
  },
  async scoreArt(sessionId, candidateId, distances) {
    try {
      const response = await scoreCandidateArtwork(sessionId, candidateId, { distances });
      if (response.status !== 200) throw new ApiError(response.status, {});
      return response.data;
    } catch (error) {
      // 409: the person decided it, or it is not a close call any more. The card stays as it is; this is not an error.
      if (error instanceof ApiError && error.status === 409) return undefined;
      throw error;
    }
  },
  async verifyCover(sessionId, candidateId, file, quad) {
    try {
      const response = await verifyCoverArtwork(sessionId, candidateId, { file, quad });
      if (response.status !== 200) throw new ApiError(response.status, {});
      return response.data;
    } catch (error) {
      // 409: decided, re-answered, not a close call, or nothing with a cover to compare. The card stays as it is.
      if (error instanceof ApiError && error.status === 409) return undefined;
      throw error;
    }
  },
  async readShelf(sessionId, snapId, file, signal) {
    // Only the picture goes: the server reads the shelf from it alone (the Vision line texts are not sent).
    const response = await readShelfSnap(sessionId, snapId, { file }, signal ? { signal } : undefined);
    if (response.status !== 200) throw new ApiError(response.status, {});
    return response.data.candidates;
  },
};
