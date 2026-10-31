// Mounted once, high in the signed-in tree (the tabs layout, next to SnapStoreBridge): makes sure sign-out, unpair and account deletion
// wipe the feature prints, saves a print when a picture card is confirmed, backs prints up to the PC (editors) and restores them on a new
// phone, and drops the prints of items that were removed. Renders nothing.
import { useEffect } from 'react';
import { AppState, Platform } from 'react-native';
import { useGetMe } from '@/api/generated/account/account';
import { registerLocalDataClearer } from '@/features/me/localData';
import { getOwnedCache } from '@/features/offline/ownedCache';
import { inLibrary } from '@/features/offline/ownedIndex';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { useLibraryStore } from '@/state/library';
import { useCardsStore } from '../cards/cardsStore';
import { getSnapStore } from '../pipeline/snapStore';
import { loadSession } from '../session/sessionApi';
import { getTrayStore } from '../tray/storage';
import { setActiveFingerprints } from './active';
import { backupPrints, dropRemovedItems, recomputeStale, restBackup, restorePrints } from './backup';
import { deviceCover, deviceMaxDistance, deviceVision, discardCover, type CoverSource } from './device';
import type { PrintVision } from './matcher';
import { ConfirmationRecorder } from './recorder';
import { clearFingerprintData, getFingerprintStore } from './store';

/** Covers fetched per sync to bring prints of another revision up to date: a few at a time, never a burst of downloads. */
const RECOMPUTE_PER_SYNC = 25;

export function FingerprintBridge({ vision, cover }: { vision?: PrintVision; cover?: CoverSource } = {}) {
  // The resolved library the screens show; what this device remembers while the list cannot load, so matching
  // and restoring still work offline.
  const { libraryId: resolved } = useActiveLibraryId();
  const stored = useLibraryStore((s) => s.activeLibraryId);
  const libraryId = resolved ?? stored ?? undefined;
  const me = useGetMe();
  const profile = me.data?.status === 200 ? me.data.data : undefined;
  const userId = profile?.id;
  const role = profile?.memberships.find((m) => m.libraryId === libraryId)?.role;
  const canEdit = role === 'owner' || role === 'editor';

  useEffect(() => registerLocalDataClearer(clearFingerprintData), []);

  // Matching, saving and restoring: scanning happens on the phone only, so the web build keeps no prints.
  useEffect(() => {
    if (!libraryId || !userId || Platform.OS === 'web') return;
    const store = getFingerprintStore({ userId, libraryId });
    const printVision = vision ?? deviceVision();
    setActiveFingerprints({ store, vision: printVision, maxDistance: deviceMaxDistance });
    const recorder = new ConfirmationRecorder({
      store,
      vision: printVision,
      snaps: getSnapStore(),
      cards: useCardsStore,
      sessionId: () => (libraryId ? getTrayStore(libraryId).getState().sessionId : null),
      loadSession,
    });
    const stopRecording = recorder.start();

    const owned = getOwnedCache(libraryId);
    const dropRemoved = () =>
      void dropRemovedItems({
        store,
        // What you have, not what you want or sold: the copy holds every item with its status, and a print of a sold or wishlisted item goes.
        ownedItemIds: new Set(owned.state.index.entries().filter(inLibrary).map((e) => e.itemId)),
        syncedAt: owned.state.syncedAt,
      });
    const restore = async () => {
      await restorePrints({ store, remote: restBackup(libraryId) });
      dropRemoved();
      await recomputeStale({ store, vision: printVision, cover: cover ?? deviceCover, discard: discardCover, limit: RECOMPUTE_PER_SYNC });
    };
    void restore();
    const stopOwned = owned.subscribe(dropRemoved);
    const app = AppState.addEventListener('change', (state) => {
      if (state === 'active') void restore();
    });
    return () => {
      stopRecording();
      stopOwned();
      app.remove();
      setActiveFingerprints(undefined);
    };
  }, [libraryId, userId, vision, cover]);

  // Backup: editors only. It runs now, when a new print is saved, and when the app comes back to the front.
  useEffect(() => {
    if (!libraryId || !userId || !canEdit || Platform.OS === 'web') return;
    const store = getFingerprintStore({ userId, libraryId });
    const remote = restBackup(libraryId);
    let running = false;
    const run = async () => {
      if (running || store.unsynced().length === 0) return;
      running = true;
      try {
        await backupPrints({ store, canEdit, remote });
      } finally {
        running = false;
      }
    };
    void run();
    const stopStore = store.subscribe(() => void run());
    const app = AppState.addEventListener('change', (state) => {
      if (state === 'active') void run();
    });
    return () => {
      stopStore();
      app.remove();
    };
  }, [libraryId, userId, canEdit]);

  return null;
}
