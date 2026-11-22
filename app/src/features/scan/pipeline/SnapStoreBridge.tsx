// Mounted once, high in the signed-in tree (the tabs layout, next to OwnedDataBridge). It does two jobs and renders nothing:
// - makes sure unpairing or signing out empties the snap store, even when the scanner was not opened in this run;
// - keeps the server's reads of shelf photos going for as long as the scan is open, not as long as the scanner screen is (#834):
//   closing the scanner for Review does not abort one, and a snap a killed app left waiting is sent without the scanner opening.
//   A library switch stops them.
import { useEffect } from 'react';
import { useStore } from 'zustand';
import { registerLocalDataClearer } from '@/features/me/localData';
import { useLibraryStore } from '@/state/library';
import { clearPipelineData } from './clearPipelineData';
import { createDevicePipelineDeps } from './device';
import { getPipelineStore } from './pipelineStore';
import { resumeShelfUploads, stopShelfUploads } from './shelfUpgrade';

export function SnapStoreBridge() {
  useEffect(() => registerLocalDataClearer(clearPipelineData), []);
  const store = getPipelineStore();
  const active = useLibraryStore((s) => s.activeLibraryId);
  // The libraries with a shelf photo waiting for the server (a string, so the bridge renders only when the set changes).
  const waiting = useStore(store, (s) => [...new Set(s.snaps.filter((n) => n.shelfUpload).map((n) => n.libraryId))].sort().join('|'));

  useEffect(() => {
    if (active) stopShelfUploads(store, { except: active }); // a library switch: the old library's reads stop
  }, [store, active]);

  useEffect(() => {
    for (const libraryId of waiting ? waiting.split('|') : []) {
      if (!active || libraryId === active) resumeShelfUploads(createDevicePipelineDeps(libraryId));
    }
  }, [active, waiting]);
  return null;
}
