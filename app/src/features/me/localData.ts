// Wiping what this device holds locally: the stored sign-in, the scan tray, any offline index, and cached answers.
// Other features (the offline cache) register their own clearer so unpairing and account deletion leave nothing behind.
import type { QueryClient } from '@tanstack/react-query';
import { endSession } from '@/lib/auth/session';
import { clearTrayData } from '@/features/scan/tray/storage';

type Clearer = () => void | Promise<void>;
const clearers = new Set<Clearer>();

/** Returns a function that removes the clearer again. */
export function registerLocalDataClearer(clearer: Clearer): () => void {
  clearers.add(clearer);
  return () => clearers.delete(clearer);
}

export async function clearLocalData(queryClient: QueryClient): Promise<void> {
  await endSession(); // clears the Keychain and the in-memory access token
  clearTrayData(); // every library's tray, not just the one open
  for (const clearer of clearers) await clearer();
  queryClient.clear();
}
