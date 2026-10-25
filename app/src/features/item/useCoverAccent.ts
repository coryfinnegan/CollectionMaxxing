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
  const [accent, setAccent] = useState<string | null>(() => (coverUri ? (accents.get(itemId) ?? null) : null));
  useEffect(() => {
    if (!coverUri) {
      setAccent(null);
      return;
    }
    const cached = accents.get(itemId);
    if (cached !== undefined) {
      setAccent(cached);
      return;
    }
    let live = true;
    getColors(coverUri, { cache: true, key: itemId })
      .then((result) => pickSource(result))
      .catch(() => null)
      .then((source) => {
        // A superseded answer (the cover changed mid-flight) must not poison the cache.
        if (!live) return;
        accents.set(itemId, source);
        setAccent(source);
      });
    return () => {
      live = false;
    };
  }, [itemId, coverUri]);
  return accent;
}
