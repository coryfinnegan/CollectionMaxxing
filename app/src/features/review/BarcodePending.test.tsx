// A barcode scan lands in Review like every other capture (#452): resolved at scan time with its match
// preselected, waiting for the commit — never straight into the library.
import { act, fireEvent, screen } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { announcementFor, useCardsStore } from '@/features/scan/cards/cardsStore';
import { createDiscardOutbox, setDiscardOutbox } from '@/features/scan/session/discards';
import { createSessionCountersStore, setSessionCountersStore } from '@/features/scan/session/sessionCounters';
import { setSessionContextStorage } from '@/features/scan/session/sessionContext';
import { createPipelineStore, setPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { setTrayStore } from '@/features/scan/tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { renderWithProviders } from '@/test/render';
import { canCommit, defaultChecked, groupOf, reviewable } from './grouping';
import { createProvisionalOutbox, setProvisionalOutbox } from './provisionals';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';

const SESSION = 'a0000000-0000-4000-8000-000000000001';

// What a barcode scan resolves to at scan time: the exact edition, its match preselected, no items yet.
const barcodeHit = (): CandidateDto =>
  candidate({
    source: 'barcode',
    shotKind: 'barcode',
    rawCode: '9780441172719',
    resolution: 'matched',
    tier: 'T0',
    decision: 'accepted',
    release: release(1, 'Dune', { format: 'paperback' }),
    createdItemId: null,
  });

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

/** One tap, flushed before the next (state updates inside a sheet need their own act). */
const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  useCardsStore.getState().reset();
  setSessionContextStorage(new MemoryKeyValueStore());
  setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
  setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
  setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  setSessionContextStorage(undefined);
  setProvisionalOutbox(undefined);
  setDiscardOutbox(undefined);
  setTrayStore(undefined);
  setPipelineStore(undefined);
  setSessionCountersStore(undefined);
});

describe('barcode scans wait in Review', () => {
  it('counts a barcode hit as to review, never as added', () => {
    const hit = barcodeHit();

    expect(announcementFor(hit)).toBe('new');
    expect(reviewable([hit])).toEqual([hit]);
    expect(groupOf(hit)).toBe('ready');
    expect(defaultChecked(hit, 'ready')).toBe(true);
    expect(canCommit(hit, 'ready')).toBe(true);
  });

  it('shows the barcode hit in Review with its match preselected and commits it there', async () => {
    seed([barcodeHit()]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onOpenResult={jest.fn()} />);

    expect(await screen.findByText('Ready · 1')).toBeTruthy();
    await press('1 item ready. Show it');
    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.getByLabelText('Add Dune').props.accessibilityState.checked).toBe(true);

    fireEvent.press(await screen.findByLabelText('Add 1 item'));

    await screen.findByText('Added 1 item');
    expect(reviewMock.commits).toHaveLength(1);
  });
});
