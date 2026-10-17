// The pipeline's calls to the API, through the generated client. Both are JSON: the one place a picture could leave
// the phone (the photo upload) is CAM-3c's and is not here.
import { identifyItems, scoreCandidateArtwork } from '@/api/generated/scanning/scanning';
import type { CandidateDto, IdentifyRequest } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { createSessionOnce } from '../session/sessionApi';

export interface ArtDistance {
  releaseId: string;
  distance: number;
}

export interface PipelineApi {
  createSession(libraryId: string): Promise<string>;
  /** One call per snap. The server answers every item it could settle at once and streams the rest. */
  identify(sessionId: string, request: IdentifyRequest): Promise<CandidateDto[]>;
  /** The artwork distances for one close call. Resolves to the re-scored candidate, or undefined when the server leaves it as it is (409). */
  scoreArt(sessionId: string, candidateId: string, distances: ArtDistance[]): Promise<CandidateDto | undefined>;
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
};
