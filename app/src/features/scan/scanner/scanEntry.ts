// Whether the Scan tab's first-run coach was dismissed. Dismissed is permanent: the flag lives on the device
// (not per library or account), so signing out never brings the coach back.
import { MemoryKeyValueStore, type KeyValueStore } from '../tray/trayStore';

const SEEN_KEY = 'scan-coach-v1:seen';

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'scan-entry' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let storage: KeyValueStore | undefined;
const backend = (): KeyValueStore => (storage ??= openStorage());

export function hasSeenScanCoach(): boolean {
  try {
    return backend().getString(SEEN_KEY) === '1';
  } catch {
    return false;
  }
}

export function dismissScanCoach(): void {
  try {
    backend().set(SEEN_KEY, '1');
  } catch {
    // Storage unavailable: the coach shows again next time.
  }
}

/** Tests swap in their own storage (and start clean). */
export function setScanEntryStorage(next: KeyValueStore | undefined) {
  storage = next;
}
