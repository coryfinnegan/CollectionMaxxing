// The segment neighbors a T2 search carries (design §8(d)): the same picture segment,
// agreed on a release, as that release's work ids. A ranking bias toward them on the
// server, never a filter: an unknown id matches nothing and the search stays plain.
import type { CandidateDto } from '@/api/generated/models';

/** The most neighbor works a search may carry. A segment holds at most 40 items, so this never binds a real one. */
export const MAX_CONTEXT_WORKS = 40;

type Neighbor = Pick<CandidateDto, 'id' | 'segmentId' | 'release'>;

/** The neighbor works of this row: same segment, resolved to a release, this row excluded, each work once. */
export function contextWorkIdsFor(row: Pick<CandidateDto, 'id' | 'segmentId'>, cards: readonly Neighbor[]): string[] {
  if (!row.segmentId) return [];
  const works: string[] = [];
  const seen = new Set<string>();
  for (const c of cards) {
    if (c.id === row.id || c.segmentId !== row.segmentId) continue;
    const workId = c.release?.workId;
    if (!workId || seen.has(workId)) continue;
    seen.add(workId);
    works.push(workId);
    if (works.length >= MAX_CONTEXT_WORKS) break;
  }
  return works;
}
