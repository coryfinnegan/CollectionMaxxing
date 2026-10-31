import { needsBorrower, statusPatch } from './statusPatch';

describe('statusPatch', () => {
  it('clears who it was lent to, and when, when the item stops being lent out', () => {
    expect(statusPatch('lent', 'owned', '2026-10-30')).toEqual({ status: 'owned', lentTo: null, lentOn: null });
    expect(statusPatch('lent', 'wishlist', '2026-10-30')).toEqual({ status: 'wishlist', lentTo: null, lentOn: null });
  });

  it('keeps the lend fields empty for any other status, so no stale name can linger', () => {
    expect(statusPatch('owned', 'wishlist', '2026-10-30')).toEqual({ status: 'wishlist', lentTo: null, lentOn: null });
  });

  it('lends out with the borrower named and today as the date', () => {
    expect(statusPatch('owned', 'lent', '2026-10-30', 'Sam')).toEqual({ status: 'lent', lentTo: 'Sam', lentOn: '2026-10-30' });
  });

  it('trims the name it was given', () => {
    expect(statusPatch('owned', 'lent', '2026-10-30', '  Sam  ').lentTo).toBe('Sam');
  });

  it('leaves an item that is already lent out alone when Lent out is chosen again', () => {
    expect(statusPatch('lent', 'lent', '2026-10-30')).toEqual({ status: 'lent' });
  });
});

describe('needsBorrower', () => {
  it('is true only when an item that is not lent out is being lent out', () => {
    expect(needsBorrower('owned', 'lent')).toBe(true);
    expect(needsBorrower('wishlist', 'lent')).toBe(true);
    expect(needsBorrower('lent', 'lent')).toBe(false);
    expect(needsBorrower('owned', 'wishlist')).toBe(false);
    expect(needsBorrower('lent', 'owned')).toBe(false);
  });
});
