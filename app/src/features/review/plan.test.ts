import { addCount, addLabel, commitBody, committable, effective, emptyDraft, picked, selectedPick, type Draft } from './plan';
import { candidate, release } from './testData';

const ready = (id: string, n: number) => candidate({ id, release: release(n, `Title ${n}`) });
const owned = (id: string, n: number) => candidate({ id, release: release(n, `Title ${n}`), alreadyOwnedCount: 1 });
const editions = [release(10, 'Suddenly You', { year: 2001 }), release(11, 'Suddenly You', { year: 2003 })];
const pick = (id: string) => candidate({ id, resolution: 'ambiguous', release: release(9, 'Guess'), alternatives: editions as never });

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

  it('counts a staged pick-one selection, beside the checked rows (#440)', () => {
    const list = [ready('a', 1), pick('p')];
    expect(addCount(list, emptyDraft())).toBe(1);
    const draft: Draft = { ...emptyDraft(), picks: { p: editions[1]!.id } };
    expect(selectedPick(draft, list[1]!)).toBe(editions[1]);
    expect(picked(list, draft).map((c) => c.id)).toEqual(['p']);
    expect(addCount(list, draft)).toBe(2);
    // The commit resolves staged picks first; they are not committable as they stand.
    expect(committable(list, draft).map((c) => c.id)).toEqual(['a']);
  });

  it('counts the engine preselection until the person picks another edition (#440)', () => {
    const row = candidate({
      id: 'p',
      resolution: 'ambiguous',
      release: release(9, 'Guess'),
      alternatives: editions as never,
      coverPick: { releaseId: editions[0]!.id, reason: 'cover_match' },
    });
    expect(selectedPick(emptyDraft(), row)).toBe(editions[0]);
    expect(addCount([row], emptyDraft())).toBe(1);
    const changed: Draft = { ...emptyDraft(), picks: { p: editions[1]!.id } };
    expect(selectedPick(changed, row)).toBe(editions[1]);
  });

  it('ignores staged picks that name no real alternative, or no pick-one row (#440)', () => {
    const list = [pick('p'), ready('a', 1)];
    const draft: Draft = { ...emptyDraft(), picks: { p: '8098d60b-a5a8-4ff1-9942-f0a1fdfd6024', a: editions[0]!.id } };
    expect(selectedPick(draft, list[0]!)).toBeNull();
    expect(selectedPick(draft, list[1]!)).toBeNull();
    expect(picked(list, draft)).toEqual([]);
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

  it('turns the choices into the one commit body: Add to, Status, and only the lines that differ', () => {
    const draft: Draft = {
      ...emptyDraft(),
      addTo: 'shelf',
      status: 'wishlist',
      overrides: { b: { locationId: 'box', condition: 'good' }, c: { status: 'owned' }, d: { locationId: 'shelf', status: 'wishlist' } },
    };
    expect(commitBody(draft, ['a', 'b', 'c', 'd'])).toEqual({
      addTo: 'shelf',
      status: 'wishlist',
      overrides: [
        { candidateId: 'b', locationId: 'box', condition: 'good' },
        { candidateId: 'c', status: 'owned' },
      ],
    });
  });

  it('leaves out overrides for rows that are not being added', () => {
    const draft: Draft = { ...emptyDraft(), overrides: { x: { condition: 'good' } } };
    expect(commitBody(draft, ['a']).overrides).toBeUndefined();
  });

  it('sends no Add to or Status when nothing differs from the defaults', () => {
    expect(commitBody(emptyDraft(), ['a'])).toEqual({});
  });
});
