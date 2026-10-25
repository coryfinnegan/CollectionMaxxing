// Close calls (docs/02 "Artwork comparison with Apple feature prints", item 2): the server says two or three releases are
// too close to tell apart by text. The phone downloads their artwork thumbnails from the server's own cover route,
// feature-prints them and the item's crop, and reports only the distances. Nothing here uploads a picture, and any failure
// leaves the card as it was (Pick one): the step returns undefined and the caller does nothing.
import type { CandidateDto } from '@/api/generated/models';
import type { FeaturePrint, Rect } from '@/native/cm-vision';
import { authHeaders, baseUrl } from '@/lib/auth/session';
import type { ArtDistance } from './pipelineApi';

/** The few alternatives worth comparing. */
export const MAX_COMPARED = 3;

export interface ArtVision {
  featurePrint(imageUri: string, cropBox?: Rect): Promise<FeaturePrint>;
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
}

export interface ArtDeps {
  vision: ArtVision;
  /** Fetches a picture from this server into the phone's temp storage and returns where it is. The request carries the sign-in. */
  download(request: { url: string; headers: Record<string, string> }): Promise<string>;
  /** Deletes a downloaded thumbnail once it has been compared. */
  discard(uri: string): Promise<void>;
}

/** What the item looks like on the phone: its crop, or its box on the still. */
export interface ItemPicture {
  uri: string;
  box?: Rect;
}

interface Alternative {
  releaseId: string;
  artworkUrl: string;
}

/** The alternatives the server offered artwork for. The alternatives arrive as loose JSON, so every field is checked. */
export function artworkOf(candidate: Pick<CandidateDto, 'alternatives'>): Alternative[] {
  const raw = candidate.alternatives as unknown;
  if (!Array.isArray(raw)) return [];
  const out: Alternative[] = [];
  for (const entry of raw.slice(0, MAX_COMPARED)) {
    const e = (entry ?? {}) as Record<string, unknown>;
    if (typeof e.releaseId === 'string' && typeof e.artworkUrl === 'string') out.push({ releaseId: e.releaseId, artworkUrl: e.artworkUrl });
  }
  return out;
}

/** What the server's cover route can name: letters, digits and a few marks. No escapes, so no `%2e%2e` or `%2f` can hide a path change. */
const SEGMENT = /^[A-Za-z0-9._~-]+$/;

/**
 * Only this server's own artwork route is fetched, and only with this server's sign-in. The path must be a normalized one
 * under `/media/`: no `..` or `.` segment, no escapes, no backslash, no query, nothing else. A full address, or any other route
 * on this server (such as `/api/...`), is never requested: the phone does not talk to third parties, and the token does not go
 * to them or to routes the server did not mean for artwork.
 */
export function ownArtworkUrl(url: string): string | undefined {
  if (!url.startsWith('/media/')) return undefined;
  const segments = url.slice('/media/'.length).split('/');
  const ok = segments.every((segment) => SEGMENT.test(segment) && segment !== '.' && segment !== '..');
  return ok ? `${baseUrl()}${url}` : undefined;
}

/**
 * The distances between the item and each alternative's artwork, or undefined when any step fails or there is nothing to
 * compare. All or nothing: a partial set would score some alternatives with the art term and others without.
 */
export async function compareArtwork(deps: ArtDeps, candidate: CandidateDto, picture: ItemPicture): Promise<ArtDistance[] | undefined> {
  const alternatives = artworkOf(candidate);
  if (alternatives.length === 0) return undefined;
  const downloaded: string[] = [];
  try {
    const mine = await deps.vision.featurePrint(picture.uri, picture.box);
    const distances: ArtDistance[] = [];
    for (const alternative of alternatives) {
      const url = ownArtworkUrl(alternative.artworkUrl);
      if (!url) return undefined;
      const uri = await deps.download({ url, headers: await authHeaders('GET') });
      downloaded.push(uri);
      const theirs = await deps.vision.featurePrint(uri);
      const distance = await deps.vision.featurePrintDistance(mine, theirs);
      if (!Number.isFinite(distance)) return undefined;
      distances.push({ releaseId: alternative.releaseId, distance });
    }
    return distances;
  } catch {
    return undefined;
  } finally {
    await Promise.all(downloaded.map((uri) => deps.discard(uri).catch(() => undefined)));
  }
}
