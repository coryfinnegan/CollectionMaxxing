// What Add leaves behind, and what it must not: scans still on the phone survive it, the rows of the closed scan do not
// leak into the next one, the phone's own debts (queued keeps and discards) reach the server first, and the offline
// "do I own this" copy follows. Review end to end against the mock API.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import * as ownedSync from '@/features/offline/sync';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { pictureCounts, pictureLines } from '@/features/scan/pipeline/lines';
import { createPipelineStore, getPipelineStore, setPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { createDiscardOutbox, getDiscardOutbox, isDiscarded, setDiscardOutbox } from '@/features/scan/session/discards';
import { createSessionCountersStore, setSessionCountersStore } from '@/features/scan/session/sessionCounters';
import { setSessionContextStorage } from '@/features/scan/session/sessionContext';
import { setTrayStore, getTrayStore } from '@/features/scan/tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { renderWithProviders } from '@/test/render';
import { createProvisionalOutbox, getProvisionalOutbox, setProvisionalOutbox, setProvisionalTransport, type ProvisionalTransport } from './provisionals';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';

const SESSION = 'a0000000-0000-4000-8000-000000000001';
const SESSION_2 = 'a0000000-0000-4000-8000-000000000010';
const alien = [release(11, 'Alien', { year: 1979 }), release(12, 'Aliens', { year: 1986 }), release(13, 'Alien 3', { year: 1992 })];

const seedSession = (id: string, list: CandidateDto[]) => {
  scanMock.sessions.set(id, { id, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};
const seed = (list: CandidateDto[]) => seedSession(SESSION, list);

const mario = () => candidate({ release: release(2, 'Super Mario 64', { format: 'game_cartridge' }) });
const zelda = () => candidate({ release: release(3, 'Zelda', { format: 'game_cartridge', year: 1998 }) });
const lost = () => candidate({ resolution: 'not_found', release: null, rawCode: '000111' });
const looking = (rawCode: string) => candidate({ resolution: 'pending', release: null, rawCode });

const open = async (sessionId: string = SESSION) => {
  await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={sessionId} onOpenResult={jest.fn()} />);
};

/** One tap, flushed before the next (state updates inside a sheet need their own act). */
const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

/** A tray with codes the server already has and codes still waiting on the phone, in the session Review shows. */
const trayWith = (sent: string[], waiting: string[]) => {
  const store = getTrayStore(MOCK_LIBRARY_ID);
  store.getState().setSessionId(SESSION);
  for (const value of sent) store.getState().scan({ value, symbology: 'ean-13' });
  store.getState().plan();
  for (const batch of [...store.getState().outbox]) store.getState().confirm(batch.key);
  for (const value of waiting) store.getState().scan({ value, symbology: 'ean-13' });
  store.getState().plan();
  return store;
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

describe('Add and the scans still on the phone', () => {
  it('keeps the scans still waiting on the phone for the next session, and forgets the ones it added', async () => {
    seed([candidate({ release: release(2, 'Super Mario 64', { format: 'game_cartridge' }), rawCode: '111' })]);
    const tray = trayWith(['111'], ['222', '333']);
    await open();

    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');

    const after = tray.getState();
    expect(after.lines.map((l) => [l.code, l.copies, l.sentCopies])).toEqual([
      ['222', 1, 0],
      ['333', 1, 0],
    ]);
    expect(after.outbox.flatMap((b) => b.entries.map((e) => e.value))).toEqual(['222', '333']);
    expect(after.sessionId).toBeNull(); // the next flush starts a new session for them
  });

  it('refuses Undo once a new scan has started, so Undo never strands that scan', async () => {
    seed([mario()]);
    const tray = trayWith([], []);
    await open();
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');
    tray.getState().setSessionId(SESSION_2); // the scans that were waiting went up to a new session

    fireEvent.press(screen.getByLabelText('Undo'));

    await screen.findByText(/started a new scan/);
    expect(reviewMock.undos).toHaveLength(0);
    expect(tray.getState().sessionId).toBe(SESSION_2);
    expect(screen.queryByLabelText('Undo')).toBeNull();
  });
});

describe('Add and the rows of the closed scan', () => {
  it('leaves none behind, so the next scan starts clean and Add counts only its own rows', async () => {
    seed([mario(), zelda(), lost(), looking('999')]);
    await open();
    await press('2 items ready. Show them');
    await press('Add Zelda'); // untick one
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');

    expect(Object.keys(useCardsStore.getState().byId)).toEqual([]);
    expect(await screen.findByText('Nothing to review.')).toBeTruthy();

    // The next scan: a new session with one row. Zelda, unticked in the old scan, must not come back ticked.
    await screen.unmount();
    seedSession(SESSION_2, [candidate({ release: release(4, 'Tetris', { format: 'game_cartridge' }) })]);
    await open(SESSION_2);
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');
    expect(reviewMock.commits.map((c) => c.sessionId)).toEqual([SESSION, SESSION_2]);
    expect(screen.queryByText(/Nothing was added/)).toBeNull();
  });

  describe('the rows still looking up', () => {
    it('says before Add that one is left out', async () => {
      seed([mario(), looking('111')]);
      await open();
      expect(await screen.findByText('1 item is still looking up. Add leaves it out.')).toBeTruthy();
    });

    it('counts several', async () => {
      seed([mario(), looking('111'), looking('222')]);
      await open();
      expect(await screen.findByText('2 items are still looking up. Add leaves them out.')).toBeTruthy();
    });

    it('says nothing when everything is looked up', async () => {
      seed([mario()]);
      await open();
      await screen.findByLabelText('Add 1 item');
      expect(screen.queryByText(/still looking up\. Add/)).toBeNull();
    });
  });
});

describe('Add and what the phone still owes the server', () => {
  it('sends a discard that was queued offline before it commits, so the discarded row is never added', async () => {
    const ghost = candidate({ release: release(5, 'Ghost', { format: 'game_cartridge' }), decision: 'accepted' });
    seed([mario(), ghost]);
    await open();
    await screen.findByLabelText('Add 2 items');
    // Accepted from a scanner card, then discarded with no connection: gone on the phone, still accepted on the server.
    await act(async () => {
      useCardsStore.getState().remove(ghost.id);
    });
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: ghost.id });

    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');

    const sent = scanMock.sessions.get(SESSION)!.candidates.find((c) => c.id === ghost.id)!;
    expect(sent.decision).toBe('rejected');
    expect(sent.createdItemId).toBeNull();
    expect(isDiscarded(ghost.id)).toBe(false);
  });

  it('sends a keep that was queued offline before it commits', async () => {
    const row = lost();
    seed([mario(), row]);
    const order: string[] = [];
    const transport: ProvisionalTransport = {
      uploadCrop: async () => ({ assetId: 'asset-1', cropUrl: '/media/covers/asset-1' }),
      keep: async (_sessionId, candidateId) => {
        order.push(reviewMock.commits.length === 0 ? 'keep before commit' : 'keep after commit');
        return { provisional: { id: 'p1' }, candidate: { ...row, id: candidateId, decision: 'rejected' } } as never;
      },
    };
    setProvisionalTransport(transport);
    try {
      await open();
      await screen.findByText('Not found · 1');
      getProvisionalOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: row.id });

      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');

      expect(order).toEqual(['keep before commit']);
      expect(getProvisionalOutbox().list(MOCK_LIBRARY_ID)).toHaveLength(0);
    } finally {
      setProvisionalTransport(undefined);
    }
  });
});

describe('Add and the offline copy of what the library owns', () => {
  it('syncs after Add and after Undo, so "do I own this" knows at once', async () => {
    const sync = jest.spyOn(ownedSync, 'syncLibrary').mockResolvedValue({ ok: true, pages: 1, changed: 1 });
    try {
      seed([mario()]);
      await open();
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');
      await waitFor(() => expect(sync).toHaveBeenCalledTimes(1));
      expect(sync).toHaveBeenLastCalledWith(MOCK_LIBRARY_ID);

      fireEvent.press(screen.getByLabelText('Undo'));
      await screen.findByText(/Undone/);
      await waitFor(() => expect(sync).toHaveBeenCalledTimes(2));
    } finally {
      sync.mockRestore();
    }
  });

  it('does not sync when the commit failed', async () => {
    const sync = jest.spyOn(ownedSync, 'syncLibrary').mockResolvedValue({ ok: true, pages: 1, changed: 1 });
    try {
      seed([mario()]);
      reviewMock.failNextCommit = true;
      await open();
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText(/Nothing was added/);
      expect(sync).not.toHaveBeenCalled();
    } finally {
      sync.mockRestore();
    }
  });
});

describe('Skip on a row that came from a picture', () => {
  it('leaves the picture done, not "processing" for ever', async () => {
    const row = candidate({ resolution: 'ambiguous', release: alien[0], alternatives: alien as never, localId: 'item-1' });
    seed([row, mario()]);
    getPipelineStore().getState().addSnap({
      snapId: 'snap-1',
      libraryId: MOCK_LIBRARY_ID,
      trigger: 'shutter',
      createdAt: 1,
      thumbUri: 'file:///tmp/snap.jpg',
      phase: 'read',
      sent: true,
      sessionId: SESSION,
      items: [{ localId: 'item-1', text: 'Alien', title: 'Alien', payload: { localId: 'item-1', text: 'Alien' }, candidateId: row.id }],
      unreadableCount: 0,
    });
    await open();

    fireEvent.press(await screen.findByLabelText('Skip Alien'));
    await waitFor(() => expect(screen.queryByText('Pick one · 1')).toBeNull());

    const lines = pictureLines(getPipelineStore().getState().snaps, Object.values(useCardsStore.getState().byId), true);
    expect(pictureCounts(lines)).toEqual({ items: 0, processing: 0 });
  });
});
