// Mounted once, high in the signed-in tree (the tabs layout, next to OwnedDataBridge): makes sure unpairing or signing out
// empties the snap store, even when the scanner was not opened in this run. Renders nothing.
import { useEffect } from 'react';
import { registerLocalDataClearer } from '@/features/me/localData';
import { clearPipelineData } from './clearPipelineData';

export function SnapStoreBridge() {
  useEffect(() => registerLocalDataClearer(clearPipelineData), []);
  return null;
}
