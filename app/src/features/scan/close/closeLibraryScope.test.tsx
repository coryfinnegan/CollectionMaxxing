// The Close sheet counts, and Discard clears, only the open library's waiting picture snaps: the other library's
// snaps are neither counted nor wiped. (Kept apart from closeScanner.test.tsx: a test that runs the real Discard flow
// breaks `screen` queries for the tests after it in the same file.)
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { Button } from '@/ui';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { picItemOf } from '../pipeline/payload';
import { createPipelineStore, setPipelineStore, type PicSnap } from '../pipeline/pipelineStore';
import { readItem } from '../pipeline/testKit';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { CloseScannerSheet } from './CloseScannerSheet';
import { useCloseScanner } from './useCloseScanner';

const SESSION = 'a0000000-0000-4000-8000-0000000000aa';
/** The library the scanner is not open on. */
const OTHER_LIBRARY_ID = 'f0000000-0000-4000-8000-000000000002';

let leave: jest.Mock;
let review: jest.Mock;

function Harness({ store, libraryId }: { store: ReturnType<typeof createTrayStore>; libraryId: string }) {
  const close = useCloseScanner({ store, libraryId, onLeave: leave, onReview: review });
  return (
    <>
      <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
      {close.state.phase === 'idle' ? <Button label="Close scanner" onPress={close.request} /> : null}
    </>
  );
}

const trayWithItems = (n: number) => {
  const store = createTrayStore({ storage: new MemoryKeyValueStore(), newKey: () => 'k', now: () => 1 });
  store.getState().setSessionId(SESSION);
  for (let i = 0; i < n; i += 1) store.getState().scan({ value: `code-${i}`, symbology: 'ean-13' });
  return store;
};

const waitingSnap = (snapId: string, libraryId: string, titles: string[]): PicSnap => ({
  snapId,
  libraryId,
  trigger: 'shutter',
  createdAt: 1,
  thumbUri: 'file:///snaps/seed.jpg',
  phase: 'read',
  sent: false,
  items: titles.map((title) => picItemOf(readItem(title))),
  unreadableCount: 0,
  engine: 'apple_model',
});

const seedPipeline = (...snaps: PicSnap[]) => {
  const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(pipeline);
  for (const snap of snaps) pipeline.getState().addSnap(snap);
  return pipeline;
};

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  leave = jest.fn();
  review = jest.fn();
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 1, candidates: [], answers: new Map(), status: 'open' });
});
afterEach(() => {
  setTrayStore(undefined);
  setPipelineStore(undefined);
});

describe('closing the scanner with another library waiting', () => {
  it('counts only this library\'s picture items alongside the tray', async () => {
    seedPipeline(waitingSnap('snap-other', OTHER_LIBRARY_ID, ['Dune', 'Cujo']), waitingSnap('snap-mine', MOCK_LIBRARY_ID, ['Emma', 'Dune', 'Hamnet']));
    await renderWithProviders(<Harness store={trayWithItems(1)} libraryId={MOCK_LIBRARY_ID} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    expect(await screen.findByLabelText('Review 4 items')).toBeTruthy();
  });

  it('Discard keeps the other library\'s waiting snaps', async () => {
    const pipeline = seedPipeline(waitingSnap('snap-other', OTHER_LIBRARY_ID, ['Dune']), waitingSnap('snap-mine', MOCK_LIBRARY_ID, ['Emma']));
    await renderWithProviders(<Harness store={trayWithItems(1)} libraryId={MOCK_LIBRARY_ID} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    await act(async () => {
      fireEvent.press(await screen.findByLabelText('Discard'));
    });
    await waitFor(() => expect(leave).toHaveBeenCalled());
    expect(pipeline.getState().snaps.map((s) => s.snapId)).toEqual(['snap-other']);
  });
});
