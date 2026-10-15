// Mounted once, high in the signed-in tree (the tabs layout): keeps the offline owned copy fresh and makes sure
// unpairing or deleting the account empties it. Renders nothing.
import { useEffect } from 'react';
import { registerLocalDataClearer } from '@/features/me/localData';
import { useOwnedSync } from './hooks';
import { clearOwnedData } from './ownedCache';

export function OwnedDataBridge() {
  useOwnedSync();
  useEffect(() => registerLocalDataClearer(clearOwnedData), []);
  return null;
}
