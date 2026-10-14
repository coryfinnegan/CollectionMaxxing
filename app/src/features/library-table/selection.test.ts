import { clearSelection, clickRow, emptySelection, selectedInOrder, togglePage } from './selection';

const order = ['a', 'b', 'c', 'd', 'e'];
const ids = (s: ReturnType<typeof emptySelection>) => selectedInOrder(s, order);

describe('row selection', () => {
  it('selects only the clicked row on a plain click', () => {
    let s = clickRow(emptySelection(), order, 'b', {});
    s = clickRow(s, order, 'd', {});
    expect(ids(s)).toEqual(['d']);
  });

  it('selects a range forward from the last click on shift-click', () => {
    let s = clickRow(emptySelection(), order, 'b', {});
    s = clickRow(s, order, 'd', { shift: true });
    expect(ids(s)).toEqual(['b', 'c', 'd']);
  });

  it('selects a range backward from the last click on shift-click', () => {
    let s = clickRow(emptySelection(), order, 'd', {});
    s = clickRow(s, order, 'b', { shift: true });
    expect(ids(s)).toEqual(['b', 'c', 'd']);
  });

  it('keeps the anchor for a second shift-click', () => {
    let s = clickRow(emptySelection(), order, 'b', {});
    s = clickRow(s, order, 'e', { shift: true });
    s = clickRow(s, order, 'c', { shift: true });
    expect(ids(s)).toEqual(['b', 'c']);
  });

  it('selects just the row on shift-click when nothing was clicked before', () => {
    expect(ids(clickRow(emptySelection(), order, 'c', { shift: true }))).toEqual(['c']);
  });

  it('toggles a row on ctrl-click without touching the others', () => {
    let s = clickRow(emptySelection(), order, 'a', {});
    s = clickRow(s, order, 'c', { toggle: true });
    expect(ids(s)).toEqual(['a', 'c']);
    s = clickRow(s, order, 'a', { toggle: true });
    expect(ids(s)).toEqual(['c']);
  });

  it('adds a range to the selection on ctrl-shift-click', () => {
    let s = clickRow(emptySelection(), order, 'a', {});
    s = clickRow(s, order, 'c', { toggle: true });
    s = clickRow(s, order, 'e', { toggle: true, shift: true });
    expect(ids(s)).toEqual(['a', 'c', 'd', 'e']);
  });

  it('selects every loaded row, then clears them, with the page toggle', () => {
    let s = togglePage(emptySelection(), order);
    expect(ids(s)).toEqual(order);
    s = togglePage(s, order);
    expect(ids(s)).toEqual([]);
  });

  it('selects the rest of the page when only some rows are selected', () => {
    const s = togglePage(clickRow(emptySelection(), order, 'b', {}), order);
    expect(ids(s)).toEqual(order);
  });

  it('drops ids that are no longer loaded', () => {
    const s = clickRow(emptySelection(), order, 'b', {});
    expect(selectedInOrder(s, ['a', 'c'])).toEqual([]);
  });

  it('clears everything', () => {
    expect(ids(clearSelection())).toEqual([]);
  });
});
