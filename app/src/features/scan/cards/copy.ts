// What a result card says, as plain data so it can be tested without rendering (docs/06 copy rules:
// sentence case, say what happened and what to do next, no exclamation marks).
import type { CandidateDto, ReleaseFormat } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { reshootOf } from '@/features/review/guidance';
import { UNKNOWN_ITEM, unresolvedTitleOf } from '@/features/review/grouping';
import { ApiError } from '@/lib/http';
import { isLocalCard } from './cardsStore';
import { friendlyError } from '@/lib/problem';

export type CardCopy = {
  title: string;
  /** Format or platform, and the year when known. */
  subtitle: string;
  /** The line that matters: "Already own — …", "Looking up…", the not-found or error copy. */
  status: string | null;
  /**
   * `pick` is a picture's close call the person has to settle; it has no Add yet. `wishlist` is a release on the library's wishlist
   * that it does not own yet: its action is Got it, not Add.
   */
  kind: 'pending' | 'owned' | 'wishlist' | 'new' | 'added' | 'pick' | 'not_found' | 'error';
};

/** A release on the wishlist the library does not own yet. Never "You own this". */
export const WISHLIST_COPY = 'On your wishlist';
/** A release the library had and let go (sold, traded, given away): informational, the card's normal actions stay. */
export const SOLD_COPY = 'You sold this';
/** Added to the owned heading of a copy that is marked missing: it is still yours, you just can't find it. */
export const MARKED_MISSING = ' · marked missing';
/** The same, as the short line a card shows under its owned line (the owned line itself keeps its room for the place). */
export const MARKED_MISSING_NOTE = 'Marked missing';

/** An auto-add that still stands: it is already in the library, with Undo on offer. */
export const ADDED_COPY = 'Added to your library';

/** A code waiting for the daily lookup allowance: no times, no jargon (docs/06). */
export const QUOTA_COPY = "We'll look this up tomorrow. Hold up the front cover to identify it now.";
export const NOT_FOUND_COPY = 'Not in the catalog. Try the front cover.';
/** A picture's item the catalog could not support. The read text is kept, so Fix it starts from it. */
export const PICTURE_NOT_FOUND_COPY = 'Not in the catalog. Fix it to search by title.';
const PICTURE_ERROR_COPY = "Couldn't look that one up. Fix it to search by title.";
const UNREADABLE_COPY = "Couldn't read that barcode. Scan it again.";
/** A valid code the lookup gave up on: the barcode read fine, the catalog could not answer it. */
export const BARCODE_LOOKUP_FAILED_COPY = "Couldn't look that one up. Try the front cover.";

/** A per-code failure becomes friendly copy through the same helper every other error uses. */
export const unreadableCopy = () => friendlyError(new ApiError(422, { code: 'unreadable_barcode' }), UNREADABLE_COPY);

/** A looked-up-but-unanswered code becomes friendly copy the same way. */
export const lookupFailedCopy = () => friendlyError(new ApiError(422, { code: 'lookup_failed' }), BARCODE_LOOKUP_FAILED_COPY);

/** "Hardcover" for a format value; a platform name or anything else passes through. */
export const formatName = (format: string | null): string | null =>
  format && format in formatLabels ? formatLabels[format as ReleaseFormat] : format;

/** "Already own — Hardcover, Living room › Shelf B" (design §7); past the first copy it says how many. */
export function alreadyOwnLine(what: string | null, where: string | null, count: number): string {
  const head = [what, where].filter(Boolean).join(', ');
  const line = head ? `Already own — ${head}` : 'Already own';
  return count > 1 ? `${line} · ${count} copies` : line;
}

/**
 * What a wishlist card knows now, from the ownership answer (the server's, or the phone's own copy offline) first and the candidate it
 * was made from second: the candidate is a picture of the moment it was scanned, and the item may have moved since (Got it from another
 * view, or before the app restarted). `nowOwned` means the release is in the library now; `itemId` is the wishlist item Got it moves.
 */
export function wishlistStateOf(
  candidate: { wishlistItemId?: string | null },
  answer: { owned: unknown[]; wishlistItemId: string | null },
): { nowOwned: boolean; itemId: string | null } {
  return { nowOwned: answer.owned.length > 0, itemId: answer.wishlistItemId ?? candidate.wishlistItemId ?? null };
}

/**
 * The one line a card says about what the library holds of a release, from an ownership answer (the server's, or the phone's own
 * copy when offline): what you own first, then the wishlist, then what you sold, then the same work in another shape. Null when
 * there is nothing to say. `owned` leads: a copy in the library is the answer the person is standing at the shelf for.
 */
export function ownershipLine(answer: {
  owned: { platform: string | null; format: string | null; locationPath: string | null; status: string }[];
  softMatches: { platform: string | null; format: string | null; locationPath: string | null }[];
  wishlistItemId: string | null;
  soldItemId: string | null;
}): { text: string; owned: boolean; note: string | null } | null {
  const first = answer.owned[0];
  if (first) {
    const line = alreadyOwnLine(first.platform ?? formatName(first.format), first.locationPath, answer.owned.length);
    return { text: line, owned: true, note: answer.owned.every((c) => c.status === 'missing') ? MARKED_MISSING_NOTE : null };
  }
  if (answer.wishlistItemId) return { text: WISHLIST_COPY, owned: false, note: null };
  if (answer.soldItemId) return { text: SOLD_COPY, owned: false, note: null };
  const soft = answer.softMatches[0];
  return soft ? { text: alsoOwnLine(soft.platform ?? formatName(soft.format), soft.locationPath), owned: false, note: null } : null;
}

/** "Also own — VHS, Bedroom" for a soft match: the same work in another shape (docs/02 §7). */
export function alsoOwnLine(what: string | null, where: string | null): string {
  const head = [what, where].filter(Boolean).join(', ');
  return head ? `Also own — ${head}` : 'Also own';
}

/** What the owned line calls the candidate: the platform for games, else the format. */
const ownedWhat = (c: CandidateDto): string | null =>
  c.release?.platform?.name ?? (c.release ? (formatLabels[c.release.format] ?? null) : null);

/** A candidate from a picture, not a barcode: it has no code, only what the phone read. */
export const isPictureCandidate = (c: Pick<CandidateDto, 'source'>) => c.source === 'picture_apple_model' || c.source === 'picture_text';

/** A repeat the phone recognized on its own; the lookup is still waiting. */
export const LOCAL_MATCH_COPY = "Seen before. We'll check it when you're online.";

function describePicture(c: CandidateDto): CardCopy {
  // Raw read text is never a title (#435): the matched release, the catalog's leading
  // alternative, or the read's own guess — else an honest unknown. The read text stays on
  // the candidate as data, where Fix it picks it up.
  const title = c.release?.title ?? unresolvedTitleOf(c) ?? UNKNOWN_ITEM;
  // No Add and no detail until the server answers: `pick` is the kind that has neither.
  if (isLocalCard(c)) return { title, subtitle: '', status: LOCAL_MATCH_COPY, kind: 'pick' };
  switch (c.resolution) {
    case 'pending':
      return { title, subtitle: '', status: reshootOf(c) ?? 'Identifying…', kind: 'pending' };
    case 'error':
      return { title, subtitle: '', status: reshootOf(c) ?? PICTURE_ERROR_COPY, kind: 'error' };
    case 'not_found':
      return { title, subtitle: '', status: reshootOf(c) ?? PICTURE_NOT_FOUND_COPY, kind: 'not_found' };
    default: {
      const release = c.release;
      const subtitle = [release?.platform?.name ?? (release ? formatLabels[release.format] : null), release?.year].filter(Boolean).join(' · ');
      // Added leads over owned: a second copy scanned in copies mode is in the library now, whatever was there before.
      if (c.createdItemId != null) return { title, subtitle, status: ADDED_COPY, kind: 'added' };
      // Owned leads: "Already own" is the answer the person is standing at the shelf for, even when the match is a close call.
      if (c.alreadyOwnedCount > 0)
        return { title, subtitle, status: alreadyOwnLine(ownedWhat(c), c.ownedAt[0] ?? null, c.alreadyOwnedCount), kind: 'owned' };
      // On the wishlist and not owned: Got it is the action. A close call has no matched release, so it never gets here.
      if (c.wishlistItemId) return { title, subtitle, status: WISHLIST_COPY, kind: 'wishlist' };
      if (c.resolution === 'ambiguous') return { title, subtitle, status: reshootOf(c) ?? 'Pick one', kind: 'pick' };
      return { title, subtitle, status: null, kind: 'new' };
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
      // "Couldn't read" is only for a code that never decoded (guidance scan_barcode); a decoded code the
      // lookup gave up on says so and points at the cover instead (#383).
      return {
        title: code,
        subtitle: '',
        status: reshootOf(c) ?? (c.guidanceCode === 'scan_barcode' ? unreadableCopy() : lookupFailedCopy()),
        kind: 'error',
      };
    case 'not_found':
      return { title: code, subtitle: '', status: reshootOf(c) ?? NOT_FOUND_COPY, kind: 'not_found' };
    default: {
      const release = c.release;
      const subtitle = [release?.platform?.name ?? (release ? formatLabels[release.format] : null), release?.year]
        .filter(Boolean)
        .join(' · ');
      if (c.createdItemId != null) return { title: release?.title ?? code, subtitle, status: ADDED_COPY, kind: 'added' };
      const owned = c.alreadyOwnedCount > 0;
      const wishlisted = !owned && !!c.wishlistItemId;
      return {
        title: release?.title ?? code,
        subtitle,
        status: owned
          ? alreadyOwnLine(ownedWhat(c), c.ownedAt[0] ?? null, c.alreadyOwnedCount)
          : wishlisted
            ? WISHLIST_COPY
            : c.resolution === 'ambiguous'
              ? 'Closest match'
              : null,
        kind: owned ? 'owned' : wishlisted ? 'wishlist' : 'new',
      };
    }
  }
}

/** "17 items · 2 processing" */
export function summaryLine(items: number, processing: number): string {
  const noun = items === 1 ? 'item' : 'items';
  return processing > 0 ? `${items} ${noun} · ${processing} processing` : `${items} ${noun}`;
}

/**
 * The scanner's one status line (#408): the tray's summary plus where the session stands. The tray's own item
 * count is the headline, so the counters' captured twin (which counts lines, not copies) stays out of it. Zero
 * buckets stay out too, and the rest run most-actionable first, so the line fits its row instead of truncating.
 */
export function reviewStatusLine(items: number, processing: number, added: number, toReview: number, owned: number): string {
  const parts = [summaryLine(items, processing)];
  if (toReview > 0) parts.push(`${toReview} to review`);
  if (owned > 0) parts.push(`${owned} owned`);
  if (added > 0) parts.push(`${added} added`);
  return parts.join(' · ');
}

/** "Review 3 items" on the scanner's Review button (#451); a bare "Review" when nothing waits. */
export function reviewButtonLabel(pending: number): string {
  if (pending <= 0) return 'Review';
  return `Review ${pending} ${pending === 1 ? 'item' : 'items'}`;
}

/** Card slide: none when Reduce Motion is on. */
export const slideDuration = (reduceMotion: boolean) => (reduceMotion ? 0 : 220);
