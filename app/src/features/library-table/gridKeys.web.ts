// Arrow-key movement in the library table: Up and Down go to the same control one row away, Left and Right to the
// next control in the row. Tab still visits every control in reading order, so nothing is reachable only by arrows.
const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),[tabindex="0"]';

type KeyEvent = { key?: string; target: EventTarget | null; preventDefault?: () => void };

const focusablesIn = (row: Element) => Array.from(row.querySelectorAll<HTMLElement>(FOCUSABLE)).filter((el) => el.getAttribute('aria-hidden') !== 'true');

export function onGridKeyDown(event: KeyEvent): void {
  const { key } = event;
  if (key !== 'ArrowUp' && key !== 'ArrowDown' && key !== 'ArrowLeft' && key !== 'ArrowRight') return;
  const target = event.target as HTMLElement | null;
  if (!target?.closest) return;
  // Text boxes and open menus keep their arrow keys.
  if (target.closest('input[type="text"],input[type="search"],textarea,select,[role="menu"],[role="listbox"],[contenteditable="true"]')) return;
  const row = target.closest('[role="row"]');
  const grid = target.closest('[role="grid"]');
  if (!row || !grid) return;
  const stops = focusablesIn(row);
  const here = stops.findIndex((el) => el === target || el.contains(target));
  if (here < 0) return;

  let next: HTMLElement | undefined;
  if (key === 'ArrowLeft' || key === 'ArrowRight') {
    next = stops[here + (key === 'ArrowRight' ? 1 : -1)];
  } else {
    const rows = Array.from(grid.querySelectorAll('[role="row"]'));
    const neighbor = rows[rows.indexOf(row) + (key === 'ArrowDown' ? 1 : -1)];
    const there = neighbor ? focusablesIn(neighbor) : [];
    next = there[Math.min(here, there.length - 1)];
  }
  if (!next) return;
  event.preventDefault?.();
  next.focus();
}
