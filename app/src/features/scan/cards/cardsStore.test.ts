import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from './cardsStore';

const card = (over: Partial<CandidateDto>) =>
  ({ id: 'c1', seq: 1, changeSeq: 1, resolution: 'pending', alreadyOwnedCount: 0, ownedAt: [], ...over }) as Partial<CandidateDto>;

beforeEach(() => useCardsStore.getState().reset());

describe('merging cards by change order', () => {
  it('never lets an older changeSeq replace a newer one, even though seq stayed the same', () => {
    useCardsStore.getState().merge([card({ changeSeq: 5, resolution: 'matched' })]);
    useCardsStore.getState().merge([card({ changeSeq: 3, resolution: 'pending' })]); // a poll arriving after the event
    expect(useCardsStore.getState().byId.c1!.resolution).toBe('matched');
  });

  it('takes an equal or newer changeSeq', () => {
    useCardsStore.getState().merge([card({ changeSeq: 3, resolution: 'pending' })]);
    useCardsStore.getState().merge([card({ changeSeq: 3, resolution: 'ambiguous' })]);
    expect(useCardsStore.getState().byId.c1!.resolution).toBe('ambiguous');
    useCardsStore.getState().merge([card({ changeSeq: 4, resolution: 'matched' })]);
    expect(useCardsStore.getState().byId.c1!.resolution).toBe('matched');
  });

  it('applies a batch oldest change first', () => {
    useCardsStore.getState().merge([card({ changeSeq: 6, resolution: 'matched' }), card({ changeSeq: 2, resolution: 'pending' })]);
    expect(useCardsStore.getState().byId.c1!.resolution).toBe('matched');
  });

  it('falls back to seq when changeSeq is missing', () => {
    useCardsStore.getState().merge([card({ seq: 4, changeSeq: undefined, resolution: 'matched' })]);
    useCardsStore.getState().merge([card({ seq: 2, changeSeq: undefined, resolution: 'pending' })]);
    expect(useCardsStore.getState().byId.c1!.resolution).toBe('matched');
  });
});

describe('ending a committed session', () => {
  it('drops the session’s candidates and keeps the phone’s own cards for pictures not sent yet', () => {
    useCardsStore.getState().merge([card({ id: 'c1' }), card({ id: 'c2' })]);
    useCardsStore.getState().showLocal(card({ id: 'local:snap-1:item-1', localId: 'item-1' }) as CandidateDto);

    useCardsStore.getState().endSession();

    expect(Object.keys(useCardsStore.getState().byId)).toEqual([]);
    expect(Object.keys(useCardsStore.getState().local)).toEqual(['local:snap-1:item-1']);
  });
});
