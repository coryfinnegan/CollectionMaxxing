// What a result card says, as plain data so it can be tested without rendering (docs/06 copy rules:
// sentence case, say what happened and what to do next, no exclamation marks).
import type { CandidateDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { ApiError } from '@/lib/http';
import { isLocalCard } from './cardsStore';
import { friendlyError } from '@/lib/problem';

export type CardCopy = {
  title: string;
  /** Format or platform, and the year when known. */
  subtitle: string;
  /** The line that matters: "You own 1", "Looking up…", the not-found or error copy. */
  status: string | null;
  /** `pick` is a picture's close call the person has to settle; it has no Add yet. */
  kind: 'pending' | 'owned' | 'new' | 'pick' | 'not_found' | 'error';
};

/** A code waiting for the daily lookup allowance: no times, no jargon (docs/06). */
export const QUOTA_COPY = "We'll look this up tomorrow. Hold up the front cover to identify it now.";
export const NOT_FOUND_COPY = 'Not in the catalog. Try the front cover.';
/** A picture's item the catalog could not support. The read text is kept, so Fix it starts from it. */
export const PICTURE_NOT_FOUND_COPY = 'Not in the catalog. Fix it to search by title.';
const PICTURE_ERROR_COPY = "Couldn't look that one up. Fix it to search by title.";
const UNREADABLE_COPY = "Couldn't read that barcode. Scan it again.";

/** A per-code failure becomes friendly copy through the same helper every other error uses. */
export const unreadableCopy = () => friendlyError(new ApiError(422, { code: 'unreadable_barcode' }), UNREADABLE_COPY);

export const ownedCopy = (count: number) => `You own ${count}`;

/** A candidate from a picture, not a barcode: it has no code, only what the phone read. */
export const isPictureCandidate = (c: Pick<CandidateDto, 'source'>) => c.source === 'picture_apple_model' || c.source === 'picture_text';

/** A repeat the phone recognized on its own; the lookup is still waiting. */
export const LOCAL_MATCH_COPY = "Seen before. We'll check it when you're online.";

function describePicture(c: CandidateDto): CardCopy {
  const read = c.visibleText?.trim() || 'Unknown item';
  // No Add and no detail until the server answers: `pick` is the kind that has neither.
  if (isLocalCard(c)) return { title: c.release?.title ?? read, subtitle: '', status: LOCAL_MATCH_COPY, kind: 'pick' };
  switch (c.resolution) {
    case 'pending':
      return { title: read, subtitle: '', status: 'Identifying…', kind: 'pending' };
    case 'error':
      return { title: read, subtitle: '', status: PICTURE_ERROR_COPY, kind: 'error' };
    case 'not_found':
      return { title: read, subtitle: '', status: PICTURE_NOT_FOUND_COPY, kind: 'not_found' };
    default: {
      const release = c.release;
      const subtitle = [release?.platform?.name ?? (release ? formatLabels[release.format] : null), release?.year].filter(Boolean).join(' · ');
      // Owned leads: "You own 1" is the answer the person is standing at the shelf for, even when the match is a close call.
      if (c.alreadyOwnedCount > 0) return { title: release?.title ?? read, subtitle, status: ownedCopy(c.alreadyOwnedCount), kind: 'owned' };
      if (c.resolution === 'ambiguous') return { title: release?.title ?? read, subtitle, status: 'Pick one', kind: 'pick' };
      return { title: release?.title ?? read, subtitle, status: null, kind: 'new' };
    }
  }
}

export function describeCandidate(c: CandidateDto): CardCopy {
  if (isPictureCandidate(c)) return describePicture(c);
  const code = c.rawCode ? `Code ${c.rawCode}` : 'Unknown item';
  switch (c.resolution) {
    case 'pending':
      return { title: code, subtitle: '', status: c.pendingReason === 'quota' ? QUOTA_COPY : 'Looking up…', kind: 'pending' };
    case 'error':
      return { title: code, subtitle: '', status: unreadableCopy(), kind: 'error' };
    case 'not_found':
      return { title: code, subtitle: '', status: NOT_FOUND_COPY, kind: 'not_found' };
    default: {
      const release = c.release;
      const subtitle = [release?.platform?.name ?? (release ? formatLabels[release.format] : null), release?.year]
        .filter(Boolean)
        .join(' · ');
      const owned = c.alreadyOwnedCount > 0;
      return {
        title: release?.title ?? code,
        subtitle,
        status: owned ? ownedCopy(c.alreadyOwnedCount) : c.resolution === 'ambiguous' ? 'Closest match' : null,
        kind: owned ? 'owned' : 'new',
      };
    }
  }
}

/** "17 items · 2 processing" */
export function summaryLine(items: number, processing: number): string {
  const noun = items === 1 ? 'item' : 'items';
  return processing > 0 ? `${items} ${noun} · ${processing} processing` : `${items} ${noun}`;
}

/** Card slide: none when Reduce Motion is on. */
export const slideDuration = (reduceMotion: boolean) => (reduceMotion ? 0 : 220);
