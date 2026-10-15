import { addCount, addLabel, bulkSteps, committable, effective, emptyDraft, type Draft } from './plan';
import { candidate, release } from './testData';

const ready = (id: string, n: number) => candidate({ id, release: release(n, `Title ${n}`) });
const owned = (id: string, n: number) => candidate({ id, release: release(n, `Title ${n}`), alreadyOwnedCount: 1 });

describe('review plan', () => {
  it('counts exactly the checked rows', () => {
    const list = [ready('a', 1), ready('b', 2), owned('c', 3)];
    const draft: Draft = { ...emptyDraft(), checked: {} };
    expect(addCount(list, draft)).toBe(2); // ready rows start checked, owned waits
    expect(addCount(list, { ...draft, checked: { b: false } })).toBe(1);
    expect(addCount(list, { ...draft, checked: { c: true } })).toBe(3);
  });

  it('never commits undecided pick-one or not-found rows, even when ticked', () => {
    const list = [
      ready('a', 1),
      candidate({ id: 'p', resolution: 'ambiguous', release: release(9, 'Guess') }),
      candidate({ id: 'n', resolution: 'not_found' }),
      candidate({ id: 'w', resolution: 'pending' }),
    ];
    const draft: Draft = { ...emptyDraft(), checked: { p: true, n: true, w: true } };
    expect(committable(list, draft).map((c) => c.id)).toEqual(['a']);
    expect(addCount(list, draft)).toBe(1);
  });

  it('writes the count into the button copy', () => {
    expect(addLabel(11)).toBe('Add 11 items');
    expect(addLabel(1)).toBe('Add 1 item');
  });

  it('applies Add to and Status to every line, and a per-line override wins', () => {
    const draft: Draft = { ...emptyDraft(), addTo: 'shelf', status: 'wishlist', overrides: { b: { locationId: 'box', status: 'owned' } } };
    expect(effective(draft, 'a')).toMatchObject({ locationId: 'shelf', status: 'wishlist' });
    expect(effective(draft, 'b')).toMatchObject({ locationId: 'box', status: 'owned' });
    // "Not placed" for one line is an override too.
    expect(effective({ ...draft, overrides: { c: { locationId: null } } }, 'c').locationId).toBeNull();
  });

  it('turns the choices into one bulk call for everyone and one per distinct override', () => {
    const draft: Draft = {
      ...emptyDraft(),
      addTo: 'shelf',
      overrides: { b: { locationId: 'box' }, c: { locationId: 'box' }, d: { locationId: 'shelf' } },
    };
    const steps = bulkSteps(draft, ['ia', 'ib', 'ic', 'id'], { a: 'ia', b: 'ib', c: 'ic', d: 'id' });
    expect(steps).toEqual([
      { ids: ['ia', 'ib', 'ic', 'id'], patch: { locationId: 'shelf' } },
      { ids: ['ib', 'ic'], patch: { locationId: 'box' } },
    ]);
  });

  it('makes no bulk call when nothing differs from the new-item defaults', () => {
    expect(bulkSteps(emptyDraft(), ['ia'], { a: 'ia' })).toEqual([]);
  });
});
