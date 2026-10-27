// What the phone holds that the server has not seen: unsent codes plus the items of unsent snaps.
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { createPipelineStore, setPipelineStore } from '../pipeline/pipelineStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { pendingLocalCount } from './useSessionCounters';

const snap = (snapId: string, sent: boolean, items: { localId: string }[]) => ({
  snapId,
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter' as const,
  createdAt: 1,
  thumbUri: 'file:///snaps/s-still.jpg',
  phase: 'read' as const,
  sent,
  items: items.map((i) => ({ localId: i.localId, text: i.localId, title: i.localId, payload: { localId: i.localId } as never })),
  unreadableCount: 0,
});

function setup() {
  const tray = createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 });
  setTrayStore(tray);
  const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(pipeline);
  return { tray, pipeline };
}

afterEach(() => {
  setTrayStore(undefined);
  setPipelineStore(undefined);
});

describe('pendingLocalCount', () => {
  it('counts unsent codes and the items of unsent snaps', () => {
    const { tray, pipeline } = setup();
    tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    tray.getState().scan({ value: 'code-2', symbology: 'ean-13' });
    pipeline.getState().addSnap(snap('s1', false, [{ localId: 'a' }, { localId: 'b' }]));
    pipeline.getState().addSnap(snap('s2', true, [{ localId: 'c' }]));

    expect(pendingLocalCount(tray.getState(), pipeline.getState().snaps, MOCK_LIBRARY_ID)).toBe(4);
  });

  it('counts a snap once it exists, even before its first item is read', () => {
    const { tray, pipeline } = setup();
    pipeline.getState().addSnap({ ...snap('s1', false, []), phase: 'reading' });

    expect(pendingLocalCount(tray.getState(), pipeline.getState().snaps, MOCK_LIBRARY_ID)).toBe(1);
  });

  it('ignores another library’s snaps', () => {
    const { tray, pipeline } = setup();
    pipeline.getState().addSnap({ ...snap('s1', false, [{ localId: 'a' }]), libraryId: 'library-2' });

    expect(pendingLocalCount(tray.getState(), pipeline.getState().snaps, MOCK_LIBRARY_ID)).toBe(0);
  });
});
