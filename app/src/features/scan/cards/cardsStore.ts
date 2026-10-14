// Result cards: the session's candidates by id, merged from the barcode responses, the event stream and the
// polling fallback. The same candidate arriving twice (response + stream) is one card.
import { create } from 'zustand';
import type { CandidateDto } from '@/api/generated/models';

export type CardEvent = 'owned' | 'new' | 'not_found';

/** What the phone should say about a candidate once it is no longer pending; undefined for no sound at all. */
export function announcementFor(c: Pick<CandidateDto, 'resolution' | 'alreadyOwnedCount'>): CardEvent | undefined {
  if (c.resolution === 'not_found') return 'not_found';
  if (c.resolution === 'matched' || c.resolution === 'ambiguous') return c.alreadyOwnedCount > 0 ? 'owned' : 'new';
  return undefined;
}

type CardsState = {
  byId: Record<string, CandidateDto>;
  /** When each card first appeared (1, 2, 3...), so an update never moves a card to the front. */
  order: Record<string, number>;
  /** Candidates already announced with a haptic, so a repeat or a reconnect stays quiet. */
  announced: Record<string, true>;
  /** Merges candidates. Returns the ones that deserve a haptic now (first time they leave pending). */
  merge(incoming: Partial<CandidateDto>[], options?: { announce?: boolean }): { id: string; event: CardEvent }[];
  setDecision(id: string, candidate: CandidateDto): void;
  remove(id: string): void;
  reset(): void;
};

export const useCardsStore = create<CardsState>((set, get) => ({
  byId: {},
  order: {},
  announced: {},
  merge(incoming, { announce = true } = {}) {
    const byId = { ...get().byId };
    const order = { ...get().order };
    const announced = { ...get().announced };
    const fresh: { id: string; event: CardEvent }[] = [];
    const ascending = [...incoming].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
    for (const part of ascending) {
      if (!part.id) continue;
      const current = byId[part.id];
      // An older copy of a candidate (a replayed event) never overwrites a newer one.
      if (current && part.seq !== undefined && part.seq < current.seq) continue;
      const merged = { ...current, ...part } as CandidateDto;
      byId[part.id] = merged;
      order[part.id] ??= Object.keys(order).length + 1;
      const event = announcementFor(merged);
      if (event && !announced[part.id]) {
        announced[part.id] = true;
        if (announce) fresh.push({ id: part.id, event });
      }
    }
    set({ byId, order, announced });
    return fresh;
  },
  setDecision: (id, candidate) => set({ byId: { ...get().byId, [id]: candidate } }),
  remove(id) {
    const { [id]: _removed, ...rest } = get().byId;
    set({ byId: rest });
  },
  reset: () => set({ byId: {}, order: {}, announced: {} }),
}));

/** Newest first, by when the card first appeared. */
export const sortedCards = (byId: Record<string, CandidateDto>, order: Record<string, number>): CandidateDto[] =>
  Object.values(byId).sort((a, b) => (order[b.id] ?? 0) - (order[a.id] ?? 0));

export const processingCount = (cards: CandidateDto[]) => cards.filter((c) => c.resolution === 'pending').length;
