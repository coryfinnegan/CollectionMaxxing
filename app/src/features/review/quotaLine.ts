// The "quota paused" counters line (issue #339, design §12): while the daily lookup allowance is spent,
// waiting codes are counted, never dropped, and the drain looks them up when the quota resets.
import type { CandidateDto } from '@/api/generated/models';

/** Rows waiting for the quota: still pending, with the server's quota reason. */
export const quotaPending = (list: readonly CandidateDto[]): CandidateDto[] =>
  list.filter((c) => c.resolution === 'pending' && c.pendingReason === 'quota');

/** "quota paused — 2 pending". Null when nothing waits, so there is no line to show. */
export const quotaPausedLine = (count: number): string | null => (count > 0 ? `quota paused — ${count} pending` : null);
