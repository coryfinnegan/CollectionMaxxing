// A scan's skipped rows (#709), as pure functions: Review hides them, but the person can open them, see why each was skipped
// and put one back. The server skips a read on its own (a repeat, a lost place, a fragment, a spine the catalog has nothing for)
// and says which in `skipReason`; a row the person skipped has none.
import type { CandidateDto, SkipReason } from '@/api/generated/models';
import { UNKNOWN_ITEM, unresolvedTitleOf } from './grouping';

export type SkippedRow = { candidate: CandidateDto; reason: SkipReason | null };

/** The reason in plain words. `person` is a row the person skipped themselves. */
export const SKIP_REASON_COPY: Record<NonNullable<SkipReason> | 'person', string> = {
  repeat: 'Read twice',
  place: "Couldn't tell where it sat",
  fragment: 'Only part of a title',
  unfound_spine: 'Nothing in the catalog matched',
  person: 'You skipped it',
};

export const skipReasonLabel = (reason: SkipReason | null): string => SKIP_REASON_COPY[reason ?? 'person'];

/** Rows that were skipped and did not become items, in the order they were scanned. */
export const skippedCandidates = (list: readonly CandidateDto[]): CandidateDto[] =>
  list.filter((c) => c.decision === 'rejected' && !c.createdItemId).sort((a, b) => a.seq - b.seq);

export const skippedRows = (list: readonly CandidateDto[]): SkippedRow[] =>
  skippedCandidates(list).map((candidate) => ({ candidate, reason: candidate.skipReason ?? null }));

/** What was read: the matched title, else the best guess at one, else the text the phone read, else its code. */
export function skippedTitleOf(c: CandidateDto): string {
  const text = c.visibleText?.trim();
  return c.release?.title ?? unresolvedTitleOf(c) ?? (text || (c.rawCode ? `Code ${c.rawCode}` : UNKNOWN_ITEM));
}
