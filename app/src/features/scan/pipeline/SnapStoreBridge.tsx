// Mounted once, high in the signed-in tree (the tabs layout, next to OwnedDataBridge). It does two jobs and renders nothing:
// - makes sure unpairing or signing out empties the snap store, even when the scanner was not opened in this run;
// - keeps the server's reads of shelf photos going for as long as the scan is open, not as long as the scanner screen is (#834):
//   closing the scanner for Review does not abort one, and a snap a killed app left waiting is sent without the scanner opening.
//   A library switch stops them; the app coming back to the front runs what is due.
import { useEffect } from 'react';
import { AppState } from 'react-native';
import { useStore } from 'zustand';
import { registerLocalDataClearer } from '@/features/me/localData';
import { useActiveLibraryId } from '@/features/onboarding/useLibraries';
import { clearPipelineData } from './clearPipelineData';
import { createDevicePipelineDeps } from './device';
import { getPipelineStore } from './pipelineStore';
import type { PipelineDeps } from './SnapPipeline';
import { resumeShelfUploads, runShelfUploads, stopShelfUploads } from './shelfUpgrade';

export function SnapStoreBridge({ makeDeps = createDevicePipelineDeps }: { makeDeps?: (libraryId: string) => PipelineDeps } = {}) {
  useEffect(() => registerLocalDataClearer(clearPipelineData), []);
  const store = getPipelineStore();
  const active = useActiveLibraryId().libraryId ?? null;
  const waiting = useStore(store, (s) => s.snaps.some((n) => n.libraryId === active && n.shelfUpload));

  useEffect(() => {
    if (active) stopShelfUploads(store, { except: active }); // a library switch: the old library's reads stop
  }, [store, active]);

  // Only the active library's waiting snaps are sent, and none until there is one.
  useEffect(() => {
    if (active && waiting) resumeShelfUploads(makeDeps(active));
  }, [active, waiting, makeDeps]);

  useEffect(() => {
    if (!active) return;
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') runShelfUploads(store, active);
    });
    return () => subscription.remove();
  }, [store, active]);
  return null;
}
