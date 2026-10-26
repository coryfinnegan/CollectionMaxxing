// The counters bar: the snapshot plus the local queue, badged offline when the server cannot be reached.
import { screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import { createPipelineStore, setPipelineStore } from '../pipeline/pipelineStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { createSessionCountersStore, getSessionCountersStore, setSessionCountersStore } from './sessionCounters';
import { SessionCountersBar } from './SessionCountersBar';
import { pendingLocalCount } from './useSessionCounters';

const SESSION = 'session-1';

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
  setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() }));
  return { tray, pipeline };
}

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

describe('SessionCountersBar', () => {
  beforeEach(setup);
  afterEach(() => {
    setTrayStore(undefined);
    setPipelineStore(undefined);
    setSessionCountersStore(undefined);
  });

  const bar = () => renderWithProviders(<SessionCountersBar libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} />);

  it('shows zeros before anything is scanned', async () => {
    await bar();

    expect(await screen.findByLabelText('0 captured · 0 added · 0 to review · 0 owned')).toBeTruthy();
  });

  it('lays the local queue over the last snapshot', async () => {
    const { tray, pipeline } = setup();
    getSessionCountersStore().getState().seed(SESSION, { captured: 10, added: 6, toReview: 2, owned: 1, skipped: 1 }, 9);
    tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    pipeline.getState().addSnap(snap('s1', false, [{ localId: 'a' }]));
    await bar();

    expect(await screen.findByLabelText('12 captured · 6 added · 4 to review · 1 owned')).toBeTruthy();
  });

  it('badges offline and keeps counting from the queue', async () => {
    const { tray } = setup();
    tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    tray.getState().setOnline(false);
    await bar();

    expect(await screen.findByLabelText('1 captured · 0 added · 1 to review · 0 owned. Offline: counts include scans still on this phone')).toBeTruthy();
    expect(await screen.findByLabelText('Offline')).toBeTruthy();
  });
});
