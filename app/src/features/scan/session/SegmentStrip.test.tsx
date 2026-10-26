// The segment strip: every snap in shoot order with its state, opening the viewer on tap.
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import { candidate } from '@/features/review/testData';
import { useCardsStore } from '../cards/cardsStore';
import { createPipelineStore, setPipelineStore } from '../pipeline/pipelineStore';
import { MemorySnapFiles, setSnapStore, SnapStore } from '../pipeline/snapStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { SegmentStrip } from './SegmentStrip';

const snap = (snapId: string, createdAt: number, extra = {}) => ({
  snapId,
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter' as const,
  createdAt,
  thumbUri: `file:///tmp/${snapId}.jpg`,
  phase: 'reading' as const,
  sent: false,
  items: [],
  unreadableCount: 0,
  ...extra,
});

const item = (localId: string, extra = {}) => ({ localId, text: localId, title: localId, payload: { localId } as never, ...extra });

function setup() {
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 }));
  const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(pipeline);
  setSnapStore(new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() }));
  useCardsStore.getState().reset();
  return { pipeline };
}

describe('SegmentStrip', () => {
  beforeEach(setup);
  afterEach(() => {
    setTrayStore(undefined);
    setPipelineStore(undefined);
    setSnapStore(undefined);
    useCardsStore.getState().reset();
  });

  it('shows nothing before the first snap', async () => {
    await renderWithProviders(<SegmentStrip libraryId={MOCK_LIBRARY_ID} onSelect={jest.fn()} />);

    expect(screen.queryByLabelText('Snaps in this scan')).toBeNull();
  });

  it('shows every snap in shoot order with its state', async () => {
    const { pipeline } = setup();
    pipeline.getState().addSnap(snap('new', 3, { phase: 'read', items: [item('b', { candidateId: 'c2' })], sent: true, sessionId: 's' }));
    pipeline.getState().addSnap(snap('old', 1, { phase: 'read', items: [item('a', { candidateId: 'c1' })], sent: true, sessionId: 's' }));
    useCardsStore.getState().merge(
      [
        candidate({ id: 'c1', localId: 'a', resolution: 'matched', decision: 'undecided' }),
        candidate({ id: 'c2', localId: 'b', resolution: 'ambiguous', decision: 'undecided' }),
      ],
      { announce: false },
    );
    const onSelect = jest.fn();
    await renderWithProviders(<SegmentStrip libraryId={MOCK_LIBRARY_ID} onSelect={onSelect} />);

    const cells = await screen.findAllByRole('button');
    expect(cells).toHaveLength(2);
    expect(cells[0]).toHaveProp('accessibilityLabel', 'Snap, resolved');
    expect(cells[1]).toHaveProp('accessibilityLabel', 'Snap, needs a look');
    fireEvent.press(cells[1]!);
    expect(onSelect).toHaveBeenCalledWith('new');
  });

  it('ignores another library’s snaps', async () => {
    const { pipeline } = setup();
    pipeline.getState().addSnap({ ...snap('s1', 1), libraryId: 'library-2' });
    await renderWithProviders(<SegmentStrip libraryId={MOCK_LIBRARY_ID} onSelect={jest.fn()} />);

    expect(screen.queryByLabelText('Snaps in this scan')).toBeNull();
  });
});
