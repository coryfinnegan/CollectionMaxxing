// The snap viewer pages across the scan's snaps in shoot order, each item with its state.
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import { candidate } from '@/features/review/testData';
import { useCardsStore } from '../cards/cardsStore';
import { createPipelineStore, setPipelineStore } from '../pipeline/pipelineStore';
import { getSnapStore, MemorySnapFiles, setSnapStore, SnapStore } from '../pipeline/snapStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { SegmentViewer } from './SegmentViewer';

const PHOTO = { uri: 'file:///cache/still.jpg', width: 2000, height: 1500 };

const snap = (snapId: string, createdAt: number, localIds: string[]) => ({
  snapId,
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter' as const,
  createdAt,
  thumbUri: `file:///tmp/${snapId}.jpg`,
  phase: 'read' as const,
  sent: true,
  sessionId: 'session-1',
  items: localIds.map((localId) => ({ localId, text: localId, title: localId, payload: { localId } as never, candidateId: `card-${localId}` })),
  unreadableCount: 0,
});

async function setup() {
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 }));
  const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(pipeline);
  const snaps = new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() });
  setSnapStore(snaps);
  useCardsStore.getState().reset();
  pipeline.getState().addSnap(snap('snap-old', 1, ['a']));
  pipeline.getState().addSnap(snap('snap-new', 2, ['b', 'c']));
  await snaps.saveStill('snap-old', PHOTO);
  await snaps.saveStill('snap-new', PHOTO);
  useCardsStore.getState().merge(
    [
      candidate({ id: 'card-a', localId: 'a', resolution: 'matched', decision: 'undecided', alreadyOwnedCount: 0 }),
      candidate({ id: 'card-b', localId: 'b', resolution: 'ambiguous', decision: 'undecided' }),
      candidate({ id: 'card-c', localId: 'c', resolution: 'matched', decision: 'undecided', alreadyOwnedCount: 0 }),
    ],
    { announce: false },
  );
}

describe('SegmentViewer', () => {
  beforeEach(setup);
  afterEach(() => {
    setTrayStore(undefined);
    setPipelineStore(undefined);
    setSnapStore(undefined);
    useCardsStore.getState().reset();
  });

  it('opens at the tapped snap with each item’s state', async () => {
    await renderWithProviders(<SegmentViewer libraryId={MOCK_LIBRARY_ID} initialSnapId="snap-new" onClose={jest.fn()} />);

    expect(await screen.findByText('Snap 2 of 2 · Needs a look')).toBeTruthy();
    expect(screen.getByText('1 of 2 in this photo')).toBeTruthy();
    expect(screen.getByText('Needs a look')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Next item'));
    expect(await screen.findByText('Resolved')).toBeTruthy();
  });

  it('pages across snaps in shoot order and closes', async () => {
    const onClose = jest.fn();
    await renderWithProviders(<SegmentViewer libraryId={MOCK_LIBRARY_ID} initialSnapId="snap-new" onClose={onClose} />);

    expect(await screen.findByText('Snap 2 of 2 · Needs a look')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Previous snap'));
    expect(await screen.findByText('Snap 1 of 2 · Resolved')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Next snap'));
    expect(await screen.findByText('Snap 2 of 2 · Needs a look')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalled();
  });

  it('shows one snap without a pager', async () => {
    const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
    setPipelineStore(pipeline);
    pipeline.getState().addSnap(snap('only', 1, ['a']));
    await getSnapStore().saveStill('only', PHOTO);
    await renderWithProviders(<SegmentViewer libraryId={MOCK_LIBRARY_ID} initialSnapId="only" onClose={jest.fn()} />);

    expect(await screen.findByText('Snap · Resolved')).toBeTruthy();
    expect(screen.queryByLabelText('Next snap')).toBeNull();
  });
});
