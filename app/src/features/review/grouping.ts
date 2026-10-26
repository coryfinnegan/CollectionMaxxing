// Review's grouping, as pure functions: each candidate lands in exactly one group, ordered by how much
// attention it needs (docs/06 M5).
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';

export type ReviewGroup = 'pick' | 'notFound' | 'owned' | 'ready' | 'pending';

/** Most attention first. Pending (still looking up) goes last and cannot be acted on yet. */
export const GROUP_ORDER: readonly ReviewGroup[] = ['pick', 'notFound', 'owned', 'ready', 'pending'];

export const MAX_ALTERNATIVES = 3;

/** Successes aggregate: matched rows collapse to one line, expandable, every row's state kept. */
export const isAggregated = (group: ReviewGroup): boolean => group === 'ready' || group === 'owned';

/** Skipped candidates and ones already turned into items are not part of Review. */
export const reviewable = (list: readonly CandidateDto[]): CandidateDto[] =>
  list.filter((c) => c.decision !== 'rejected' && !c.createdItemId);

export function groupOf(c: CandidateDto): ReviewGroup {
  switch (c.resolution) {
    case 'pending':
      return 'pending';
    case 'ambiguous':
      return 'pick';
    case 'not_found':
    case 'error':
      return 'notFound';
    default:
      return c.alreadyOwnedCount > 0 ? 'owned' : 'ready';
  }
}

export function groupCandidates(list: readonly CandidateDto[]): Record<ReviewGroup, CandidateDto[]> {
  const groups: Record<ReviewGroup, CandidateDto[]> = { pick: [], notFound: [], owned: [], ready: [], pending: [] };
  for (const c of [...list].sort((a, b) => a.seq - b.seq)) groups[groupOf(c)].push(c);
  return groups;
}

/** Ready rows start checked, as does anything already accepted from a scanner card. Owned and undecided rows wait for a tap. */
export const defaultChecked = (c: CandidateDto, group: ReviewGroup): boolean =>
  group === 'ready' || (group === 'owned' && c.decision === 'accepted');

/** Only rows that name a release can become items. */
export const canCommit = (c: CandidateDto, group: ReviewGroup): boolean => (group === 'ready' || group === 'owned') && !!c.release;

const asRelease = (value: unknown): ReleaseDto | undefined => {
  if (!value || typeof value !== 'object') return undefined;
  const v = value as Record<string, unknown>;
  if (typeof v.id === 'string' && typeof v.title === 'string') return v as unknown as ReleaseDto;
  return v.release ? asRelease(v.release) : undefined;
};

/** The top alternatives of an unsure match. The API sends them as releases, or as `{ release, score }`. */
export function alternativesOf(c: Pick<CandidateDto, 'alternatives'>): ReleaseDto[] {
  const raw = c.alternatives as unknown;
  if (!Array.isArray(raw)) return [];
  return raw.map(asRelease).filter((r): r is ReleaseDto => !!r).slice(0, MAX_ALTERNATIVES);
}
