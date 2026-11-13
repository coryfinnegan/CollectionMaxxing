// A scan's skipped rows in Review (#709): listed with why, and put back into review. Neutral titles throughout.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
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
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { createProvisionalOutbox, setProvisionalOutbox } from './provisionals';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';

const SESSION = 'a0000000-0000-4000-8000-000000000001';

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

const unfound = () => candidate({ resolution: 'not_found', decision: 'rejected', visibleText: 'Harbour Atlas', skipReason: 'unfound_spine' });
const twice = () => candidate({ decision: 'rejected', visibleText: 'Quiet Almanac', skipReason: 'repeat' });
const byHand = () => candidate({ decision: 'rejected', release: release(7, 'Orchard Songs', { format: 'paperback' }), skipReason: null });

const open = () => renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onOpenResult={jest.fn()} />);

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

describe('Review: skipped rows', () => {
  it('keeps skipped rows out of review but reachable from a collapsed Skipped section', async () => {
    seed([unfound(), twice(), byHand()]);
    await open();
    expect(await screen.findByText('Skipped · 3')).toBeTruthy();
    expect(screen.queryByText('Harbour Atlas')).toBeNull();
    await press('3 items skipped. Show them');
    expect(screen.getByText('Harbour Atlas')).toBeTruthy();
  });

  it('says why each row was skipped, in plain words', async () => {
    seed([unfound(), twice(), byHand()]);
    await open();
    await press('3 items skipped. Show them');
    expect(screen.getByText('Nothing in the catalog matched')).toBeTruthy();
    expect(screen.getByText('Read twice')).toBeTruthy();
    expect(screen.getByText('You skipped it')).toBeTruthy();
    expect(screen.getByText('Orchard Songs')).toBeTruthy();
  });

  it('says an owned or wishlisted release was already in the library or on the wishlist, not that you skipped it', async () => {
    seed([
      candidate({ decision: 'rejected', release: release(8, 'Lantern Hollow', { format: 'paperback' }), skipReason: 'owned', alreadyOwnedCount: 1 }),
      candidate({ decision: 'rejected', release: release(9, 'Cedar Row', { format: 'paperback' }), skipReason: 'wishlist' }),
    ]);
    await open();
    await press('2 items skipped. Show them');
    expect(screen.getByText('Already in your library')).toBeTruthy();
    expect(screen.getByText('On your wishlist')).toBeTruthy();
    expect(screen.queryByText('You skipped it')).toBeNull();
  });

  it('empties the Skipped section once Add closes the session', async () => {
    seed([candidate({ decision: 'accepted', release: release(2, 'Orchard Songs', { format: 'paperback' }) }), unfound()]);
    await open();
    await screen.findByText('Skipped · 1');
    await press('Add 1 item');
    await screen.findByText('Added 1 item');
    expect(screen.queryByText(/Skipped ·/)).toBeNull();
  });

  it('shows no Skipped section when nothing was skipped', async () => {
    seed([candidate({ release: release(2, 'Orchard Songs', { format: 'paperback' }) })]);
    await open();
    await screen.findByText('1 item ready. Show it');
    expect(screen.queryByText(/Skipped ·/)).toBeNull();
  });

  it('puts a skipped row back: the PATCH says undecided, the row leaves the list and shows in its Review group', async () => {
    const row = candidate({ resolution: 'not_found', decision: 'rejected', visibleText: 'Harbour Atlas', skipReason: 'unfound_spine' });
    seed([row]);
    const bodies: unknown[] = [];
    server.events.on('request:start', async ({ request }) => {
      if (request.method === 'PATCH') bodies.push(await request.clone().json());
    });
    await open();
    await press('1 item skipped. Show it');
    await press('Put back Harbour Atlas');
    await waitFor(() => expect(bodies).toEqual([{ decision: 'undecided' }]));
    await screen.findByText('Not found · 1');
    expect(screen.queryByText(/Skipped ·/)).toBeNull();
    expect(scanMock.sessions.get(SESSION)!.candidates[0]!.decision).toBe('undecided');
  });

  it('moves a row the server skips while Review is open to Skipped, with its reason, and Put back restores it (#809)', async () => {
    const row = candidate({ resolution: 'not_found', decision: 'undecided', visibleText: 'Harbour Atlas' });
    seed([row]);
    await open();
    await screen.findByText('Not found · 1');
    // The server's own read of the shelf photo lands late: it skipped this row (the cards keep it, rejected).
    await act(async () => {
      const mock = scanMock.sessions.get(SESSION)!.candidates[0]!;
      Object.assign(mock, { decision: 'rejected', skipReason: 'server_did_not_see', changeSeq: ++scanMock.sessions.get(SESSION)!.seq });
      useCardsStore.getState().merge([mock], { announce: false });
    });
    await screen.findByText('Skipped · 1');
    await press('1 item skipped. Show it');
    expect(screen.getByText('The server read this shelf differently')).toBeTruthy();
    await press('Put back Harbour Atlas');
    await screen.findByText('Not found · 1');
    expect(screen.queryByText(/Skipped ·/)).toBeNull();
  });

  it('offers a viewer no Put back', async () => {
    scanMock.role = 'viewer';
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    seed([unfound()]);
    await open();
    await press('1 item skipped. Show it');
    expect(screen.getByText('Harbour Atlas')).toBeTruthy();
    expect(screen.queryByLabelText(/Put back/)).toBeNull();
  });
});
