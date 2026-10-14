import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore, announcementFor } from './cardsStore';
import { describeCandidate, NOT_FOUND_COPY, slideDuration, summaryLine } from './copy';

const candidate = (over: Partial<CandidateDto>): CandidateDto =>
  ({
    id: 'c1',
    seq: 1,
    source: 'barcode',
    rawCode: '123',
    resolution: 'matched',
    alreadyOwnedCount: 0,
    ownedAt: [],
    release: { id: 'r', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [] },
    ...over,
  }) as CandidateDto;

describe('card copy', () => {
  it('says "You own 1" for an owned item', () => {
    expect(describeCandidate(candidate({ alreadyOwnedCount: 1 }))).toMatchObject({ title: 'Dune', subtitle: 'Paperback · 1990', status: 'You own 1', kind: 'owned' });
  });

  it('says nothing extra for a new item, so + Add is the next step', () => {
    expect(describeCandidate(candidate({}))).toMatchObject({ status: null, kind: 'new' });
  });

  it('uses the not-found copy, without apologies or exclamation marks', () => {
    const copy = describeCandidate(candidate({ resolution: 'not_found', release: null }));
    expect(copy.status).toBe(NOT_FOUND_COPY);
    expect(copy.status).not.toMatch(/[!]|sorry|oops/i);
  });

  it('describes a pending lookup', () => {
    expect(describeCandidate(candidate({ resolution: 'pending', release: null })).status).toBe('Looking up…');
  });

  it('reads the summary line', () => {
    expect(summaryLine(17, 2)).toBe('17 items · 2 processing');
    expect(summaryLine(1, 0)).toBe('1 item');
  });

  it('has no slide with Reduce Motion', () => {
    expect(slideDuration(true)).toBe(0);
    expect(slideDuration(false)).toBeGreaterThan(0);
  });
});

describe('cards store', () => {
  beforeEach(() => useCardsStore.getState().reset());

  it('maps resolutions to haptic events', () => {
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 2 })).toBe('owned');
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 0 })).toBe('new');
    expect(announcementFor({ resolution: 'not_found', alreadyOwnedCount: 0 })).toBe('not_found');
    expect(announcementFor({ resolution: 'pending', alreadyOwnedCount: 0 })).toBeUndefined();
    expect(announcementFor({ resolution: 'error', alreadyOwnedCount: 0 })).toBeUndefined();
  });

  it('announces a candidate once, even when it arrives twice', () => {
    const first = useCardsStore.getState().merge([candidate({})]);
    const again = useCardsStore.getState().merge([candidate({ seq: 2 })]);
    expect(first).toHaveLength(1);
    expect(again).toHaveLength(0);
    expect(Object.keys(useCardsStore.getState().byId)).toHaveLength(1);
  });

  it('announces a pending candidate when it resolves, and ignores a stale replay', () => {
    const merge = useCardsStore.getState().merge;
    expect(merge([candidate({ resolution: 'pending', release: null })])).toHaveLength(0);
    expect(merge([candidate({ seq: 3, resolution: 'matched' })])).toHaveLength(1);
    merge([candidate({ seq: 2, resolution: 'pending' })]);
    expect(useCardsStore.getState().byId.c1?.resolution).toBe('matched');
  });

  it('does not buzz for cards loaded when resuming', () => {
    expect(useCardsStore.getState().merge([candidate({})], { announce: false })).toHaveLength(0);
    expect(useCardsStore.getState().merge([candidate({ seq: 2 })])).toHaveLength(0);
  });
});
