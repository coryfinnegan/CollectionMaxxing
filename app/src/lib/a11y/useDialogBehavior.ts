// Keyboard behaviour for the app's inline panels (Add an item, Columns, Invite, filter sheets) on the web:
// focus moves into the panel when it opens, Escape closes the top one, and focus goes back to the button that opened it.
// The panels are not modal (the page behind stays usable), so focus is not trapped. On the phone this does nothing.
import { useEffect, useRef, useState } from 'react';
import { Platform, type View } from 'react-native';

/** Open panels, newest last: Escape closes only the newest, so a panel inside a panel closes first. */
const open: symbol[] = [];

export function useDialogBehavior(onClose: () => void) {
  const ref = useRef<View>(null);
  const close = useRef(onClose);
  useEffect(() => {
    close.current = onClose;
  });

  // Read during the first render: a child with autoFocus takes focus before any effect runs, and the opener is what had it before.
  const [opener] = useState(() => (Platform.OS === 'web' && typeof document !== 'undefined' ? (document.activeElement as HTMLElement | null) : null));

  useEffect(() => {
    if (Platform.OS !== 'web' || typeof document === 'undefined') return;
    const id = Symbol('panel');
    open.push(id);
    const panel = ref.current as unknown as HTMLElement | null;
    if (panel && !panel.contains(document.activeElement)) panel.focus?.({ preventScroll: true });

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented && open[open.length - 1] === id) close.current();
    };
    document.addEventListener('keydown', onKey, true); // capture: a text box can stop the event before it bubbles
    return () => {
      document.removeEventListener('keydown', onKey, true);
      open.splice(open.indexOf(id), 1);
      if (!opener) return;
      if (document.contains(opener)) {
        opener.focus?.({ preventScroll: true });
        return;
      }
      // The opener was replaced while the panel was open (a button that swaps for the panel): find its twin by name.
      const name = opener.getAttribute('aria-label');
      if (name) setTimeout(() => document.querySelector<HTMLElement>(`[aria-label="${CSS.escape(name)}"]`)?.focus({ preventScroll: true }), 0);
    };
  }, [opener]);

  return Platform.OS === 'web' ? { ref, tabIndex: -1 as const } : {};
}
