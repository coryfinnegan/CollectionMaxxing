import { reviewable } from './grouping';
import { SKIP_REASON_COPY, skippedRows, skippedTitleOf } from './skipped';
import { candidate, release } from './testData';

describe('skipped rows', () => {
  it('lists the rejected rows that did not become items, in scan order, each with its reason', () => {
    const second = candidate({ decision: 'rejected', skipReason: 'unfound_spine', visibleText: 'Marlow Atlas' });
    const first = candidate({ decision: 'rejected', skipReason: 'repeat' });
    first.seq = second.seq - 1;
    const rows = skippedRows([second, candidate(), candidate({ decision: 'rejected', createdItemId: 'item-1' }), first]);
    expect(rows.map((r) => r.candidate.id)).toEqual([first.id, second.id]);
    expect(rows.map((r) => r.reason)).toEqual(['repeat', 'unfound_spine']);
  });

  it('gives a row the person skipped no server reason', () => {
    const [row] = skippedRows([candidate({ decision: 'rejected', skipReason: null })]);
    expect(row?.reason).toBeNull();
  });

  it('leaves review as it was: a skipped row is still not reviewable', () => {
    const skipped = candidate({ decision: 'rejected', skipReason: 'place' });
    expect(reviewable([skipped, candidate()])).toHaveLength(1);
  });

  it('says why in plain words', () => {
    expect(SKIP_REASON_COPY.repeat).toBe('Read twice');
    expect(SKIP_REASON_COPY.place).toBe("Couldn't tell where it sat");
    expect(SKIP_REASON_COPY.fragment).toBe('Only part of a title');
    expect(SKIP_REASON_COPY.unfound_spine).toBe('Nothing in the catalog matched');
    expect(SKIP_REASON_COPY.owned).toBe('Already in your library');
    expect(SKIP_REASON_COPY.wishlist).toBe('On your wishlist');
    expect(SKIP_REASON_COPY.server_did_not_see).toBe('A second look left it out');
    expect(SKIP_REASON_COPY.person).toBe('You skipped it');
  });

  it('names a row by what was read: its release, then its read text, then its code', () => {
    expect(skippedTitleOf(candidate({ release: release(1, 'Orchard Songs'), visibleText: 'ORCHARD SONGS' }))).toBe('Orchard Songs');
    expect(skippedTitleOf(candidate({ visibleText: ' Marlow Atlas ' }))).toBe('Marlow Atlas');
    expect(skippedTitleOf(candidate({ rawCode: '123' }))).toBe('Code 123');
    expect(skippedTitleOf(candidate({ rawCode: null }))).toBe('Unknown item');
  });
});
