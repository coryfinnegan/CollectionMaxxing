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
  // The wire shape (CandidateAlternativeDto): a releaseId plus flat fields. Picks only read
  // id/title/year/format, and choosing PATCHes the id; the full release arrives after the pick.
  // The bare platform name rides along under its own key: ReleaseDto's platform is an object.
  if (typeof v.releaseId === 'string' && typeof v.title === 'string')
    return { ...v, id: v.releaseId, platform: null, platformName: typeof v.platform === 'string' ? v.platform : null } as unknown as ReleaseDto;
  return v.release ? asRelease(v.release) : undefined;
};

/** The top alternatives of an unsure match. The API sends them flat with a releaseId; mocks and old fixtures send releases, or `{ release, score }`. */
export function alternativesOf(c: Pick<CandidateDto, 'alternatives'>): ReleaseDto[] {
  const raw = c.alternatives as unknown;
  if (!Array.isArray(raw)) return [];
  return raw.map(asRelease).filter((r): r is ReleaseDto => !!r).slice(0, MAX_ALTERNATIVES);
}

/** The platform's name, whichever shape the alternative arrived in (a full release object or a flat wire name). */
export function platformNameOf(r: ReleaseDto): string | null {
  if (r.platform && typeof r.platform === 'object') return r.platform.name ?? null;
  const extra = r as unknown as Record<string, unknown>;
  return typeof extra.platformName === 'string' ? extra.platformName : null;
}

/** The catalog art for the option row: the release cover first, the provider artwork next. */
export function alternativeCoverOf(r: ReleaseDto): string | null {
  if (r.coverUrl) return r.coverUrl;
  const extra = r as unknown as Record<string, unknown>;
  return typeof extra.artworkUrl === 'string' ? extra.artworkUrl : null;
}

/** How many copies of this alternative are already owned (the wire shape says; a full release cannot). */
export function ownedCountOf(r: ReleaseDto): number {
  const extra = r as unknown as Record<string, unknown>;
  return typeof extra.alreadyOwnedCount === 'number' ? extra.alreadyOwnedCount : 0;
}

/**
 * The matching engine's preselected edition (#438): its release id plus the short reason
 * shown beside the row ("Cover match"). Tolerant by design: absent, blank, or naming no
 * listed alternative means no preselection, and the person taps as usual.
 */
export type Preselection = { releaseId: string; reason: string | null };

export function preselectedOf(c: Pick<CandidateDto, 'alternatives'>): Preselection | null {
  const extra = c as unknown as Record<string, unknown>;
  const releaseId = typeof extra.suggestedReleaseId === 'string' && extra.suggestedReleaseId ? extra.suggestedReleaseId : null;
  if (!releaseId) return null;
  if (!alternativesOf(c).some((r) => r.id === releaseId)) return null;
  const reason = typeof extra.suggestionReason === 'string' && extra.suggestionReason.trim() ? extra.suggestionReason.trim() : null;
  return { releaseId, reason };
}

/** What an unnamed candidate is called. Raw read text is never a title (#435). */
export const UNKNOWN_ITEM = 'Unknown item';

/** The read's own title guess, when the phone offered one. The model is a hint, never the answer. */
export function inferredTitleOf(c: Pick<CandidateDto, 'inferred'>): string | null {
  const v = c.inferred as unknown;
  if (!v || typeof v !== 'object') return null;
  const title = (v as Record<string, unknown>).title;
  return typeof title === 'string' && title.trim() ? title.trim() : null;
}

/** The most grounded name for a candidate with no matched release: the catalog's leading alternative first, the read's own guess next. Raw read text is never a title (#435). */
export function unresolvedTitleOf(c: Pick<CandidateDto, 'alternatives' | 'inferred'>): string | null {
  const [top] = alternativesOf(c);
  const catalog = top ? top.title.trim() : '';
  if (catalog) return catalog;
  return inferredTitleOf(c);
}
