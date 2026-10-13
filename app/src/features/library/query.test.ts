import { changeFilter, defaultState, PAGE_SIZE, sortLabel, toListParams } from './query';

describe('toListParams', () => {
  it('sends only what is set, newest first by default', () => {
    expect(toListParams(defaultState())).toEqual({ sort: '-addedAt', limit: PAGE_SIZE });
  });

  it('maps every filter to its query parameter', () => {
    const state = changeFilter(defaultState(), {
      q: ' dune ',
      kind: 'game',
      format: 'game_cartridge',
      platform: 'n64',
      locationId: 'loc-1',
      sort: 'title',
    });
    expect(toListParams(state)).toEqual({
      q: 'dune',
      kind: 'game',
      format: 'game_cartridge',
      platform: 'n64',
      locationId: 'loc-1',
      sort: 'title',
      limit: PAGE_SIZE,
    });
  });

  it('fixes the status for the Wishlist and Lent out views', () => {
    expect(toListParams(defaultState(), 'wishlist').status).toBe('wishlist');
    expect(toListParams(defaultState(), 'lent').status).toBe('lent');
    expect(toListParams(defaultState()).status).toBeUndefined();
  });

  it('adds the cursor when asking for a later page', () => {
    expect(toListParams({ ...defaultState(), cursor: 'abc' }).cursor).toBe('abc');
  });
});

describe('changeFilter', () => {
  it('resets the cursor so the list starts again from the top', () => {
    const next = changeFilter({ ...defaultState(), cursor: 'abc' }, { kind: 'film' });
    expect(next.cursor).toBeUndefined();
    expect(next.filters.kind).toBe('film');
  });

  it('clears a filter when it is set to undefined', () => {
    const withKind = changeFilter(defaultState(), { kind: 'film' });
    expect(changeFilter(withKind, { kind: undefined }).filters.kind).toBeUndefined();
  });
});

describe('sortLabel', () => {
  it('uses plain words', () => {
    expect(sortLabel('-addedAt')).toBe('Recently added');
    expect(sortLabel('title')).toBe('Title');
    expect(sortLabel('year')).toBe('Year');
    expect(sortLabel('platform')).toBe('Platform');
  });
});
