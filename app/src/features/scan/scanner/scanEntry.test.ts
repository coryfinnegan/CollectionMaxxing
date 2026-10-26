// The first-run coach shows once: until it is dismissed the scan entry is a first run; after, it never shows again.
import { MemoryKeyValueStore } from '../tray/trayStore';
import { dismissScanCoach, hasSeenScanCoach, setScanEntryStorage } from './scanEntry';

beforeEach(() => setScanEntryStorage(new MemoryKeyValueStore()));
afterEach(() => setScanEntryStorage(undefined));

describe('scan entry first run', () => {
  it('starts as a first run', () => {
    expect(hasSeenScanCoach()).toBe(false);
  });

  it('stays dismissed once the coach is dismissed', () => {
    dismissScanCoach();

    expect(hasSeenScanCoach()).toBe(true);
  });

  it('treats an unreadable store as a first run', () => {
    setScanEntryStorage({ getString: () => { throw new Error('locked'); }, set: () => undefined, remove: () => undefined });

    expect(hasSeenScanCoach()).toBe(false);
  });
});
