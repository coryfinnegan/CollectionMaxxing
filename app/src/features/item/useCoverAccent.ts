// One accent per item, resolved from its cover art and cached (#306). Detail
// screens only: lists never derive color. A rejection (offline, unreadable
// art) caches as no accent, so a failing cover is read once, not on every visit.
import { useEffect, useState } from 'react';
import { getColors } from 'react-native-image-colors';
import { pickSource } from './coverColor';

const accents = new Map<string, string | null>();

/** Test seam: forget every cached accent. */
export function clearCoverAccents(): void {
  accents.clear();
}

export function useCoverAccent(itemId: string, coverUri: string | null | undefined): string | null {
  // The uri joins the key so a new cover refetches instead of reusing the old tint.
  const key = coverUri ? `${itemId} ${coverUri}` : null;
  const [state, setState] = useState<{ key: string | null; accent: string | null }>(() => ({ key, accent: key ? (accents.get(key) ?? null) : null }));
  // A new cover (or none) resets the accent during the render, not in an effect.
  if (state.key !== key) setState({ key, accent: key ? (accents.get(key) ?? null) : null });
  useEffect(() => {
    if (!key || !coverUri || accents.has(key)) return;
    let live = true;
    getColors(coverUri, { cache: true, key })
      .then((result) => pickSource(result))
      .catch(() => null)
      .then((source) => {
        // A superseded answer (the cover changed mid-flight) must not poison the cache.
        if (!live) return;
        accents.set(key, source);
        setState({ key, accent: source });
      });
    return () => {
      live = false;
    };
  }, [key, coverUri]);
  return state.key === key ? state.accent : null;
}
