// Mounted once, high in the signed-in tree (the tabs layout): when the library changes, the result cards on
// screen belong to the old scan, so they are dropped. Reopening that scan loads its session again, so nothing is
// lost; without this, Library B's scanner and Review would show Library A's cards. Renders nothing.
import { useEffect, useRef } from 'react';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { useCardsStore } from './cardsStore';

export function CardsBridge() {
  const { libraryId } = useActiveLibraryId();
  const previous = useRef(libraryId);
  useEffect(() => {
    if (previous.current !== libraryId) {
      previous.current = libraryId;
      useCardsStore.getState().reset();
    }
  }, [libraryId]);
  return null;
}
