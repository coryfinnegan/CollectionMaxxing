// Result cards: the session's candidates by id, merged from the barcode responses, the event stream and the
// polling fallback. The same candidate arriving twice (response + stream) is one card.
import { create } from 'zustand';
import type { CandidateDto } from '@/api/generated/models';

export type CardEvent = 'owned' | 'new' | 'added' | 'not_found';

/** Whether the candidate was added to the library by a T0 auto-add that still stands (#341). */
export const isAutoCommitted = (c: Pick<CandidateDto, 'createdItemId'>): boolean => c.createdItemId != null;

/** What the phone should say about a candidate once it is no longer pending; undefined for no sound at all. */
export function announcementFor(
  c: Pick<CandidateDto, 'resolution' | 'alreadyOwnedCount' | 'createdItemId'>,
): CardEvent | undefined {
  if (isAutoCommitted(c)) return 'added';
  if (c.resolution === 'not_found') return 'not_found';
  if (c.resolution === 'matched' || c.resolution === 'ambiguous') return c.alreadyOwnedCount > 0 ? 'owned' : 'new';
  return undefined;
}

/** Where a copy of a candidate sits in the order of its changes. `seq` stays put when a candidate is updated, so `changeSeq` leads. */
const changeOrder = (c: Partial<Pick<CandidateDto, 'seq' | 'changeSeq'>>): number | undefined => c.changeSeq ?? c.seq;

type CardsState = {
  byId: Record<string, CandidateDto>;
  /** When each card first appeared (1, 2, 3...), so an update never moves a card to the front. */
  order: Record<string, number>;
  /** Candidates already announced with a haptic, so a repeat or a reconnect stays quiet. */
  announced: Record<string, true>;
  /**
   * Cards the phone drew itself from a local feature-print match (CAM-3e2), by id (`local:<snapId>:<localId>`). They are not the
   * server's candidates: Review, commit and every action read `byId` only, so nothing can add one. The server's candidate for the same
   * `localId` replaces its local card the moment it arrives.
   */
  local: Record<string, CandidateDto>;
  /** Merges candidates. Returns the ones that deserve a haptic now (first time they leave pending). */
  merge(incoming: Partial<CandidateDto>[], options?: { announce?: boolean }): { id: string; event: CardEvent }[];
  setDecision(id: string, candidate: CandidateDto): void;
  showLocal(card: CandidateDto): void;
  /** Takes away the local cards of one snap, or of one item of it. */
  dropLocal(snapId: string, localId?: string): void;
  remove(id: string): void;
  reset(): void;
};

export const useCardsStore = create<CardsState>((set, get) => ({
  byId: {},
  order: {},
  announced: {},
  local: {},
  merge(incoming, { announce = true } = {}) {
    const byId = { ...get().byId };
    const order = { ...get().order };
    const announced = { ...get().announced };
    const fresh: { id: string; event: CardEvent }[] = [];
    const ascending = [...incoming].sort((a, b) => (changeOrder(a) ?? 0) - (changeOrder(b) ?? 0));
    for (const part of ascending) {
      if (!part.id) continue;
      const current = byId[part.id];
      // An older copy of a candidate (a replayed event) never overwrites a newer one.
      const incomingOrder = changeOrder(part);
      if (current && incomingOrder !== undefined && incomingOrder < (changeOrder(current) ?? 0)) continue;
      const merged = { ...current, ...part } as CandidateDto;
      byId[part.id] = merged;
      order[part.id] ??= Object.keys(order).length + 1;
      const event = announcementFor(merged);
      if (event && !announced[part.id]) {
        announced[part.id] = true;
        if (announce) fresh.push({ id: part.id, event });
      }
    }
    // The server has answered for these items: its card replaces the phone's own.
    const answered = new Set(ascending.map((part) => byId[part.id!]?.localId).filter((id): id is string => !!id));
    const local = Object.fromEntries(Object.entries(get().local).filter(([, card]) => !answered.has(card.localId ?? '')));
    set({ byId, order, announced, local });
    return fresh;
  },
  showLocal(card) {
    const order = { ...get().order };
    order[card.id] ??= Object.keys(order).length + 1;
    set({ local: { ...get().local, [card.id]: card }, order });
  },
  dropLocal(snapId, localId) {
    const prefix = localId === undefined ? `${LOCAL_PREFIX}${snapId}:` : `${LOCAL_PREFIX}${snapId}:${localId}`;
    const local = Object.fromEntries(Object.entries(get().local).filter(([id]) => !id.startsWith(prefix)));
    if (Object.keys(local).length !== Object.keys(get().local).length) set({ local });
  },
  setDecision: (id, candidate) => set({ byId: { ...get().byId, [id]: candidate } }),
  remove(id) {
    const { [id]: _removed, ...rest } = get().byId;
    set({ byId: rest });
  },
  reset: () => set({ byId: {}, order: {}, announced: {}, local: {} }),
}));

export const LOCAL_PREFIX = 'local:';

/** A card the phone drew from a local match; it is shown, never acted on. */
export const isLocalCard = (c: Pick<CandidateDto, 'id'>) => c.id.startsWith(LOCAL_PREFIX);

type Shown = { byId: Record<string, CandidateDto>; local: Record<string, CandidateDto> };
/** The cards to show: the server's candidates and, until the server answers, the phone's own. */
export const shownCards = (s: Shown): Record<string, CandidateDto> => ({ ...s.local, ...s.byId });
export const withLocalCards = (s: Shown): CandidateDto[] => Object.values(shownCards(s));

/** Newest first, by when the card first appeared. */
export const sortedCards = (byId: Record<string, CandidateDto>, order: Record<string, number>): CandidateDto[] =>
  Object.values(byId).sort((a, b) => (order[b.id] ?? 0) - (order[a.id] ?? 0));

export const processingCount = (cards: CandidateDto[]) => cards.filter((c) => c.resolution === 'pending').length;
