// What a result card says, as plain data so it can be tested without rendering (docs/06 copy rules:
// sentence case, say what happened and what to do next, no exclamation marks).
import type { CandidateDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';

export type CardCopy = {
  title: string;
  /** Format or platform, and the year when known. */
  subtitle: string;
  /** The line that matters: "You own 1", "Looking up…", the not-found or error copy. */
  status: string | null;
  kind: 'pending' | 'owned' | 'new' | 'not_found' | 'error';
};

export const NOT_FOUND_COPY = 'Not in the catalogue. Try the front cover.';
const UNREADABLE_COPY = "Couldn't read that barcode. Scan it again.";

/** A per-code failure becomes friendly copy through the same helper every other error uses. */
export const unreadableCopy = () => friendlyError(new ApiError(422, { code: 'unreadable_barcode' }), UNREADABLE_COPY);

export const ownedCopy = (count: number) => `You own ${count}`;

export function describeCandidate(c: CandidateDto): CardCopy {
  const code = c.rawCode ? `Code ${c.rawCode}` : 'Unknown item';
  switch (c.resolution) {
    case 'pending':
      return { title: code, subtitle: '', status: 'Looking up…', kind: 'pending' };
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
