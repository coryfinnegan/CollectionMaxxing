import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore, announcementFor } from './cardsStore';
import { alreadyOwnLine, alsoOwnLine, BARCODE_LOOKUP_FAILED_COPY, describeCandidate, NOT_FOUND_COPY, QUOTA_COPY, receiptLineName, reviewButtonLabel, reviewStatusLine, slideDuration, summaryLine } from './copy';

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
  it('says what is owned and where for an owned item', () => {
    expect(describeCandidate(candidate({ alreadyOwnedCount: 1, ownedAt: ['Living room › Shelf 2'] }))).toMatchObject({
      title: 'Dune',
      subtitle: 'Paperback · 1990',
      status: 'Already own — Paperback, Living room › Shelf 2',
      kind: 'owned',
    });
  });

  it('names the platform for an owned game, and counts past the first copy', () => {
    const game = {
      id: 'c1',
      seq: 1,
      source: 'barcode',
      rawCode: '123',
      resolution: 'matched',
      alreadyOwnedCount: 2,
      ownedAt: ['Game room › Shelf 1'],
      release: { id: 'r', title: 'Super Mario 64', format: 'game_cartridge', year: 1996, platform: { name: 'Nintendo 64' }, creators: [] },
    } as unknown as CandidateDto;
    expect(describeCandidate(game).status).toBe('Already own — Nintendo 64, Game room › Shelf 1 · 2 copies');
  });

  it('says what is owned when no location is known', () => {
    expect(describeCandidate(candidate({ alreadyOwnedCount: 1, ownedAt: [] })).status).toBe('Already own — Paperback');
  });

  it('reads the owned line from a looked-up copy', () => {
    expect(alreadyOwnLine('Hardcover', 'Living room › Shelf B', 1)).toBe('Already own — Hardcover, Living room › Shelf B');
    expect(alreadyOwnLine('DVD', null, 1)).toBe('Already own — DVD');
    expect(alreadyOwnLine('VHS', 'Bedroom', 3)).toBe('Already own — VHS, Bedroom · 3 copies');
  });

  it('reads the soft-match line', () => {
    expect(alsoOwnLine('VHS', 'Living room › Shelf B')).toBe('Also own — VHS, Living room › Shelf B');
    expect(alsoOwnLine('DVD', null)).toBe('Also own — DVD');
  });

  it('says nothing extra for a new item, so + Add is the next step', () => {
    expect(describeCandidate(candidate({}))).toMatchObject({ status: null, kind: 'new' });
  });

  it('says an auto-added candidate is in the library, not how to add it', () => {
    expect(describeCandidate(candidate({ createdItemId: 'item-1' }))).toMatchObject({
      title: 'Dune',
      status: 'Added to your library',
      kind: 'added',
    });
  });

  it('says added even for another copy of an owned item', () => {
    expect(describeCandidate(candidate({ alreadyOwnedCount: 1, ownedAt: ['Living room › Shelf 2'], createdItemId: 'item-1' }))).toMatchObject({
      status: 'Added to your library',
      kind: 'added',
    });
  });

  it('uses the not-found copy, without apologies or exclamation marks', () => {
    const copy = describeCandidate(candidate({ resolution: 'not_found', release: null }));
    expect(copy.status).toBe(NOT_FOUND_COPY);
    expect(copy.status).not.toMatch(/[!]|sorry|oops/i);
  });

  it('describes a pending lookup', () => {
    expect(describeCandidate(candidate({ resolution: 'pending', release: null })).status).toBe('Looking up…');
  });

  it('says a code waiting for the daily allowance will be looked up tomorrow, without times or jargon', () => {
    const copy = describeCandidate(candidate({ resolution: 'pending', release: null, pendingReason: 'quota', retryAfter: '2026-10-17T00:00:00Z' }));
    expect(copy.status).toBe("We'll look this up tomorrow. Hold up the front cover to identify it now.");
    expect(copy.status).toBe(QUOTA_COPY);
    expect(copy.status).not.toMatch(/quota|UTC|\d{2}:\d{2}|[!]/i);
    expect(describeCandidate(candidate({ resolution: 'pending', release: null, pendingReason: 'lookup' })).status).toBe('Looking up…');
  });

  it("says couldn't-read only for a code that never decoded", () => {
    const copy = describeCandidate(candidate({ resolution: 'error', release: null, guidanceCode: 'scan_barcode' }));
    expect(copy.status).toBe("Couldn't read that barcode. Scan it again.");
    expect(copy.kind).toBe('error');
  });

  it('says a decoded code the lookup gave up on could not be looked up, and points at the cover', () => {
    for (const guidanceCode of ['shoot_cover', null] as const) {
      const copy = describeCandidate(candidate({ resolution: 'error', release: null, guidanceCode }));
      expect(copy.status).toBe(BARCODE_LOOKUP_FAILED_COPY);
      expect(copy.status).toBe("Couldn't look that one up. Try the front cover.");
      expect(copy.status).not.toMatch(/[!]|sorry|oops/i);
    }
  });

  it('reads the summary line', () => {
    expect(summaryLine(17, 2)).toBe('17 items · 2 processing');
    expect(summaryLine(1, 0)).toBe('1 item');
  });

  it('reads the one status line: the tray plus where the session stands', () => {
    expect(reviewStatusLine(5, 1, 2, 4, 1)).toBe('5 items · 1 processing · 4 to review · 1 owned · 2 added');
    expect(reviewStatusLine(1, 0, 0, 1, 0)).toBe('1 item · 1 to review');
    expect(reviewStatusLine(0, 0, 0, 0, 0)).toBe('0 items');
  });

  it('labels the scanner Review button with the pending count (#451)', () => {
    expect(reviewButtonLabel(3)).toBe('Review 3 items');
    expect(reviewButtonLabel(1)).toBe('Review 1 item');
    expect(reviewButtonLabel(0)).toBe('Review');
  });

  it('has no slide with Reduce Motion', () => {
    expect(slideDuration(true)).toBe(0);
    expect(slideDuration(false)).toBeGreaterThan(0);
  });
});

describe('receipt line name (#450)', () => {
  const CODE = '9781974723386';

  it('shows the resolved title once the server names it', () => {
    expect(receiptLineName(CODE, candidate({}), undefined)).toBe('Dune');
  });

  it('shows the book for a resolved-but-rejected line instead of the raw code', () => {
    expect(receiptLineName(CODE, undefined, 'The Legend of Zelda: Twilight Princess 9')).toBe('The Legend of Zelda: Twilight Princess 9');
  });

  it('shows an honest not-found state, never the raw code', () => {
    expect(receiptLineName(CODE, candidate({ resolution: 'not_found', release: null }), undefined)).toBe('Not in the catalog');
  });

  it('shows the leading pick for an ambiguous line', () => {
    const ambiguous = candidate({
      resolution: 'ambiguous',
      release: null,
      alternatives: [{ releaseId: 'r', title: 'Dune Messiah', score: 0.9 }] as never,
    });
    expect(receiptLineName(CODE, ambiguous, undefined)).toBe('Dune Messiah');
  });

  it('shows an honest state for an ambiguous line with nothing to pick', () => {
    expect(receiptLineName(CODE, candidate({ resolution: 'ambiguous', release: null, alternatives: [] }), undefined)).toBe('Needs review');
  });

  it('shows an honest state for a failed lookup, and a re-scan hint when the code never decoded', () => {
    expect(receiptLineName(CODE, candidate({ resolution: 'error', release: null, guidanceCode: null }), undefined)).toBe("Couldn't look that one up");
    expect(receiptLineName(CODE, candidate({ resolution: 'error', release: null, guidanceCode: 'scan_barcode' }), undefined)).toBe(
      "Couldn't read that barcode",
    );
  });

  it('keeps the raw code only while nothing is known yet', () => {
    expect(receiptLineName(CODE, candidate({ resolution: 'pending', release: null }), undefined)).toBe(CODE);
    expect(receiptLineName(CODE, undefined, undefined)).toBe(CODE);
  });

  it('never shows the raw code once resolution has run', () => {
    for (const resolution of ['matched', 'ambiguous', 'not_found', 'error'] as const) {
      const name = receiptLineName(CODE, candidate({ resolution, release: null, alternatives: [] }), 'Known Title');
      expect(name).not.toContain(CODE);
    }
  });
});

describe('cards store', () => {
  beforeEach(() => useCardsStore.getState().reset());

  it('maps resolutions to haptic events', () => {
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 2, createdItemId: null })).toBe('owned');
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 0, createdItemId: null })).toBe('new');
    expect(announcementFor({ resolution: 'not_found', alreadyOwnedCount: 0, createdItemId: null })).toBe('not_found');
    expect(announcementFor({ resolution: 'pending', alreadyOwnedCount: 0, createdItemId: null })).toBeUndefined();
    expect(announcementFor({ resolution: 'error', alreadyOwnedCount: 0, createdItemId: null })).toBeUndefined();
  });

  it('announces an auto-added candidate as added, even when it is also owned', () => {
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 0, createdItemId: 'item-1' })).toBe('added');
    expect(announcementFor({ resolution: 'matched', alreadyOwnedCount: 2, createdItemId: 'item-1' })).toBe('added');
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
