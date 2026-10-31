import { statusPatch } from './statusPatch';

describe('statusPatch', () => {
  it('clears who it was lent to, and when, when the item stops being lent out', () => {
    expect(statusPatch('lent', 'owned', '2026-10-30')).toEqual({ status: 'owned', lentTo: null, lentOn: null });
    expect(statusPatch('lent', 'wishlist', '2026-10-30')).toEqual({ status: 'wishlist', lentTo: null, lentOn: null });
  });

  it('keeps the lend fields empty for any other status, so no stale name can linger', () => {
    expect(statusPatch('owned', 'wishlist', '2026-10-30')).toEqual({ status: 'wishlist', lentTo: null, lentOn: null });
  });

  it('lends out with the name left blank and today as the date, like the edit form does', () => {
    expect(statusPatch('owned', 'lent', '2026-10-30')).toEqual({ status: 'lent', lentTo: null, lentOn: '2026-10-30' });
  });

  it('leaves an item that is already lent out alone when Lent out is chosen again', () => {
    expect(statusPatch('lent', 'lent', '2026-10-30')).toEqual({ status: 'lent' });
  });
});
