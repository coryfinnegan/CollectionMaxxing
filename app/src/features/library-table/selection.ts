// Pure row-selection logic for the table: click, shift-click ranges, ctrl-click toggle, select page.
export type Selection = { readonly ids: ReadonlySet<string>; readonly anchor: string | null };
export type ClickModifiers = { shift?: boolean; toggle?: boolean };

export const emptySelection = (): Selection => ({ ids: new Set(), anchor: null });
export const clearSelection = emptySelection;

/** Applies one click. `order` is the rows as shown, top to bottom. */
export function clickRow(current: Selection, order: readonly string[], id: string, mods: ClickModifiers): Selection {
  const index = order.indexOf(id);
  if (index < 0) return current;
  const anchorIndex = current.anchor ? order.indexOf(current.anchor) : -1;

  if (mods.shift && anchorIndex >= 0) {
    const [from, to] = anchorIndex <= index ? [anchorIndex, index] : [index, anchorIndex];
    const range = order.slice(from, to + 1);
    // Ctrl-shift adds the range; a plain shift-click replaces the selection with it. The anchor stays put.
    const ids = new Set(mods.toggle ? [...current.ids, ...range] : range);
    return { ids, anchor: current.anchor };
  }
  if (mods.toggle) {
    const ids = new Set(current.ids);
    if (ids.has(id)) ids.delete(id);
    else ids.add(id);
    return { ids, anchor: id };
  }
  return { ids: new Set([id]), anchor: id };
}

/** The header checkbox: selects every loaded row, or clears them when they are all selected already. */
export function togglePage(current: Selection, order: readonly string[]): Selection {
  const all = order.length > 0 && order.every((id) => current.ids.has(id));
  return all ? emptySelection() : { ids: new Set(order), anchor: current.anchor };
}

/** The selected ids that are still loaded, in table order. */
export const selectedInOrder = (current: Selection, order: readonly string[]): string[] =>
  order.filter((id) => current.ids.has(id));
