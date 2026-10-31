import { focusManager } from '@tanstack/react-query';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { Platform } from 'react-native';
import DetailRoute from '@/app/item/[id]/index';
import EditRoute from '@/app/item/[id]/edit';
import { LibraryView } from '@/features/library/LibraryView';
import { boundScanStore } from '@/features/review/boundScan';
import { MOCK_LIBRARY_ID, mockMe } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { ItemDetailScreen } from './ItemDetailScreen';
import { ItemEditScreen } from './ItemEditScreen';
import { useUndoStore } from './useItem';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: jest.fn(() => true) };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  useLocalSearchParams: () => ({ id: 'c0000000-0000-4000-8000-000000000001' }),
}));

// The native picker has no Jest implementation: the stand-in renders a marker
// and keeps its last props, so tests drive onChange the way the OS would.
jest.mock('@react-native-community/datetimepicker', () => {
  const { View } = jest.requireActual('react-native');
  const MockPicker = (props: { value: Date }) => {
    (MockPicker as unknown as { lastProps: unknown }).lastProps = props;
    return <View testID="acquired-picker" />;
  };
  return { __esModule: true, default: MockPicker };
});

type PickerChange = (event: { type: string }, date?: Date) => void;
const pickerOnChange = () =>
  (jest.requireMock('@react-native-community/datetimepicker').default as unknown as { lastProps: { onChange: PickerChange } })
    .lastProps.onChange;

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const DUNE = 'c0000000-0000-4000-8000-000000000003';
const MARIO = 'c0000000-0000-4000-8000-000000000004';
const entry = () => itemsState.entries.find((e) => e.item.id === MATRIX)!;

let patchHeaders: (string | null)[] = [];
let patchBodyReads: Promise<unknown>[] = [];
/** What each PATCH sent, in order. */
const patchBodies = () => Promise.all(patchBodyReads);
let itemReads = 0;
let itemRequests = 0;
let meReads = 0;

beforeEach(() => {
  resetItems();
  boundScanStore.getState().clear();
  useUndoStore.setState({ deleted: null });
  mockRouter.back.mockClear();
  mockRouter.push.mockClear();
  mockRouter.replace.mockClear();
  mockRouter.canGoBack.mockReturnValue(true);
  patchHeaders = [];
  patchBodyReads = [];
  itemReads = 0;
  itemRequests = 0;
  meReads = 0;
  server.events.on('request:start', ({ request }) => {
    if (request.method === 'GET' && new URL(request.url).pathname.endsWith(`/items/${MATRIX}`)) itemRequests += 1;
    if (request.method === 'PATCH') {
      patchHeaders.push(request.headers.get('If-Match'));
      patchBodyReads.push(request.clone().json());
    }
  });
  // Counted when the answer is back, so a test can wait for a refetch to land.
  server.events.on('response:mocked', ({ request }) => {
    const path = new URL(request.url).pathname;
    if (request.method === 'GET' && path.endsWith(`/items/${MATRIX}`)) itemReads += 1;
    if (request.method === 'GET' && path.endsWith('/api/v1/me')) meReads += 1;
  });
});
afterEach(() => {
  server.events.removeAllListeners();
  focusManager.setFocused(undefined);
});

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), memberships: [{ ...mockMe().memberships[0]!, role }] })));

/** A refocus of the window, which is how a web tab quietly refetches an item that is open. Resolves once the answer is in. */
async function refocusAndWaitForFailure() {
  const before = itemRequests;
  await act(async () => {
    focusManager.setFocused(false);
    focusManager.setFocused(true);
  });
  await waitFor(() => expect(itemRequests).toBeGreaterThan(before));
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
}

async function refocusAndWaitForRefetch() {
  const before = itemReads;
  await act(async () => {
    focusManager.setFocused(false);
    focusManager.setFocused(true);
  });
  await waitFor(() => expect(itemReads).toBeGreaterThan(before));
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
}

describe('ItemDetailScreen', () => {
  it('shows the facts, the location breadcrumb and the other copies', async () => {
    entry().item.locationId = 'a0000000-0000-4000-8000-000000000003';
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(await screen.findByText('Game room › Shelf 2')).toBeTruthy();
    expect(screen.getByText('Owned')).toBeTruthy();
  });

  it('moves the item to another location', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Move'));
    await fireEvent.press(await screen.findByLabelText('Shelf 1'));

    await waitFor(() => expect(entry().item.locationId).toBe('a0000000-0000-4000-8000-000000000002'));
    expect(patchHeaders).toEqual(['"v1"']);
  });

  it('badges an edition that is still unverified, and stays quiet once it is proven', async () => {
    entry().item.editionVerified = false;
    const unverified = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('Edition unverified')).toBeTruthy();
    await unverified.unmount();

    entry().item.editionVerified = true;
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(screen.queryByText('Edition unverified')).toBeNull();
  });

  it('offers a barcode scan while the edition is unverified, and stays quiet once it is proven', async () => {
    entry().item.editionVerified = false;
    const unverified = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    await fireEvent.press(await screen.findByLabelText('Scan barcode to verify edition'));
    expect(boundScanStore.getState().binding).toEqual({
      libraryId: MOCK_LIBRARY_ID,
      itemId: MATRIX,
      title: 'The Matrix',
      kind: 'itemBarcode',
    });
    expect(mockRouter.push).toHaveBeenCalledWith('/scanner');
    await unverified.unmount();

    entry().item.editionVerified = true;
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(screen.queryByLabelText('Scan barcode to verify edition')).toBeNull();
  });

  it('shows friendly copy when the item is gone', async () => {
    await renderWithProviders(<ItemDetailScreen id="missing" />);
    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.getByText("That's no longer here. Refresh and try again.")).toBeTruthy();
  });

  it('sets the notes on a plain card', async () => {
    entry().item.notes = 'Slipcover missing';
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('Notes')).toBeTruthy();
    expect(screen.getByText('Slipcover missing')).toBeTruthy();
  });

  it('says none on the notes card when there are no notes', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('Notes')).toBeTruthy();
    expect(screen.getByText('None')).toBeTruthy();
  });

  it('counts a work owned more than once in plain words', async () => {
    const matrix = entry();
    itemsState.entries.push({ ...matrix, item: { ...matrix.item, id: 'c0000000-0000-4000-8000-999999999999' } });
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('2 copies of this work')).toBeTruthy();
  });

  it('shows no copies line for a single copy', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.queryByText(/copies of this work/)).toBeNull();
  });

  it('keeps the gone state plain when the item is gone', async () => {
    await renderWithProviders(<ItemDetailScreen id="missing" />);
    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.queryByTestId('castle-state-dragon')).toBeNull();
  });

  it('deletes the item and goes back', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Delete'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.deletedAt).not.toBeNull();
    expect(useUndoStore.getState().deleted?.title).toBe('The Matrix');
  });

  it('on a fresh load, with no history to go back to, deleting lands on the library instead of nowhere', async () => {
    mockRouter.canGoBack.mockReturnValue(false);
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Delete'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(mockRouter.back).not.toHaveBeenCalled();
    expect(useUndoStore.getState().deleted?.title).toBe('The Matrix');
  });

  it('keeps Undo reachable when the deleted item then answers not found', async () => {
    // The router is a stand-in, so the screen stays up while the refetch after the delete comes back 404.
    mockRouter.canGoBack.mockReturnValue(false);
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Delete'));

    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.getByText('Deleted The Matrix')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByLabelText('Move')).toBeTruthy();
    expect(entry().item.deletedAt).toBeNull();
    expect(screen.queryByText('Deleted The Matrix')).toBeNull();
  });

  it('Undo after a delete brings the item back to the list', async () => {
    const detail = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');
    await fireEvent.press(await screen.findByLabelText('Delete'));
    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    await detail.unmount();

    await renderWithProviders(<LibraryView title="Library" />);
    expect(await screen.findByText('Deleted The Matrix')).toBeTruthy();
    expect(screen.queryByLabelText('The Matrix, DVD')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByLabelText('The Matrix, DVD')).toBeTruthy();
    expect(entry().item.deletedAt).toBeNull();
    expect(screen.queryByText('Deleted The Matrix')).toBeNull();
  });

  describe('for each role', () => {
    const editingControls = ['Edit', 'Move', 'Delete', 'Scan barcode to verify edition'];

    it.each(['owner', 'editor'] as const)('offers an %s every editing action', async (role) => {
      asRole(role);
      entry().item.editionVerified = false;
      await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

      for (const label of editingControls) expect(await screen.findByLabelText(label)).toBeTruthy();
    });

    it('offers a viewer the facts and nothing to change', async () => {
      asRole('viewer');
      entry().item.editionVerified = false;
      await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
      await screen.findByText('The Matrix');
      // Wait for the role to land, so the controls' absence is the answer and not just "not yet".
      await waitFor(() => expect(meReads).toBeGreaterThan(0));
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
      });

      for (const label of editingControls) expect(screen.queryByLabelText(label)).toBeNull();
      expect(screen.getByText('Owned')).toBeTruthy();
      expect(screen.getByText('Edition unverified')).toBeTruthy();
    });

    it('keeps the editing actions hidden until the role is known', async () => {
      server.use(http.get('*/api/v1/me', () => new Promise<never>(() => undefined)));
      await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
      await screen.findByText('The Matrix');

      expect(screen.queryByLabelText('Delete')).toBeNull();
      expect(screen.queryByLabelText('Edit')).toBeNull();
    });

    describe('on the web', () => {
      const originalOs = Platform.OS;
      afterEach(() => {
        Platform.OS = originalOs;
      });

      it('never offers the barcode scan, which only the iPhone can do, but still offers the edits', async () => {
        Platform.OS = 'web';
        entry().item.editionVerified = false;
        await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

        expect(await screen.findByLabelText('Edit')).toBeTruthy();
        expect(screen.getByText('Edition unverified')).toBeTruthy();
        expect(screen.queryByLabelText('Scan barcode to verify edition')).toBeNull();
      });
    });
  });
});

describe('ItemEditScreen', () => {
  it('saves with If-Match from the loaded ETag', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Condition Mint'));
    await fireEvent.changeText(screen.getByLabelText('Notes'), 'Slipcover missing');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(patchHeaders).toEqual(['"v1"']);
    expect(entry().item.notes).toBe('Slipcover missing');
    expect(entry().item.condition).toBe('mint');
  });

  it('after a stale ETag, shows the reload message and keeps the edits visible', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    // Someone else saves first.
    entry().version += 1;

    await fireEvent.changeText(screen.getByLabelText('Notes'), 'My edit');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    expect(await screen.findByText('Someone changed this. Reload?')).toBeTruthy();
    expect(screen.getByDisplayValue('My edit')).toBeTruthy();
    expect(mockRouter.back).not.toHaveBeenCalled();
    expect(entry().item.notes).toBeNull();

    // Reload takes the latest version, and saving then works.
    await fireEvent.press(screen.getByLabelText('Reload'));
    await waitFor(() => expect(screen.queryByText('Someone changed this. Reload?')).toBeNull());
    await fireEvent.changeText(screen.getByLabelText('Notes'), 'My edit');
    await fireEvent.press(screen.getByLabelText('Save changes'));
    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(patchHeaders).toEqual(['"v1"', '"v2"']);
  });

  describe('when someone else saves the same item', () => {
    /** B saves while A has the form open: the server moves on a version and B's notes are in. */
    const bSaves = () => {
      entry().item.notes = "B's notes";
      entry().version += 1;
    };

    it('does not let a refetch swap the version being edited: a quiet refocus keeps the ETag the form opened with', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      bSaves();

      // On the web a refocus refetches the item behind the form; the live ETag is now B's.
      await refocusAndWaitForRefetch();
      await fireEvent.press(screen.getByLabelText('Condition Mint'));
      await fireEvent.press(screen.getByLabelText('Save changes'));

      // The save still carries the version A started from, so the server refuses it instead of overwriting B.
      expect(await screen.findByText('Someone changed this. Reload?')).toBeTruthy();
      expect(patchHeaders).toEqual(['"v1"']);
      expect(entry().item.notes).toBe("B's notes");
      expect(entry().item.condition).toBeNull();
      expect(mockRouter.back).not.toHaveBeenCalled();
    });

    it('keeps the form on screen when a refetch behind it fails', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      await fireEvent.changeText(screen.getByLabelText('Notes'), 'My edit');

      server.use(http.get('*/api/v1/libraries/:libraryId/items/:id', () => HttpResponse.error()));
      await refocusAndWaitForFailure();

      expect(screen.getByDisplayValue('My edit')).toBeTruthy();
      expect(screen.queryByText("Couldn't load this item")).toBeNull();
    });

    it('refuses a second Save too, until Reload, so a refreshed ETag never lets the edit through', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      bSaves();

      await fireEvent.changeText(screen.getByLabelText('Notes'), "A's notes");
      await fireEvent.press(screen.getByLabelText('Save changes'));
      expect(await screen.findByText('Someone changed this. Reload?')).toBeTruthy();
      // The refused save refreshes the item behind the form (the live ETag is now B's).
      await refocusAndWaitForRefetch();

      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(patchHeaders).toEqual(['"v1"', '"v1"']));
      expect(await screen.findByText('Someone changed this. Reload?')).toBeTruthy();
      expect(entry().item.notes).toBe("B's notes");
      expect(mockRouter.back).not.toHaveBeenCalled();
    });

    it('Reload adopts their version, drops the old edits, and the next Save carries the new ETag', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      bSaves();
      await fireEvent.changeText(screen.getByLabelText('Notes'), "A's notes");
      await fireEvent.press(screen.getByLabelText('Save changes'));
      await screen.findByText('Someone changed this. Reload?');

      await fireEvent.press(screen.getByLabelText('Reload'));

      await waitFor(() => expect(screen.queryByText('Someone changed this. Reload?')).toBeNull());
      expect(screen.getByDisplayValue("B's notes")).toBeTruthy();
      await fireEvent.press(screen.getByLabelText('Condition Mint'));
      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(patchHeaders).toEqual(['"v1"', '"v2"']);
      expect(await patchBodies()).toEqual([{ notes: "A's notes" }, { condition: 'mint' }]);
      expect(entry().item.notes).toBe("B's notes");
      expect(entry().item.condition).toBe('mint');
    });

    it('says so, and keeps the edits, when Reload cannot reach the server', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      bSaves();
      await fireEvent.changeText(screen.getByLabelText('Notes'), "A's notes");
      await fireEvent.press(screen.getByLabelText('Save changes'));
      await screen.findByText('Someone changed this. Reload?');

      server.use(http.get('*/api/v1/libraries/:libraryId/items/:id', () => HttpResponse.error()));
      await fireEvent.press(screen.getByLabelText('Reload'));

      expect(await screen.findByText("Couldn't reload. Check your connection and try again.")).toBeTruthy();
      expect(screen.getByDisplayValue("A's notes")).toBeTruthy();
      expect(screen.getByText('Someone changed this. Reload?')).toBeTruthy();
    });
  });

  describe('what a save sends', () => {
    it('sends only the fields that were changed', async () => {
      entry().item.notes = 'Slipcover missing';
      entry().item.condition = 'good';
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.press(screen.getByLabelText('Condition Mint'));
      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(await patchBodies()).toEqual([{ condition: 'mint' }]);
      expect(entry().item.notes).toBe('Slipcover missing');
    });

    it('sends nothing, and simply leaves, when no field was edited', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(patchHeaders).toEqual([]);
    });

    it('clears notes by sending null for them alone', async () => {
      entry().item.notes = 'Slipcover missing';
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.changeText(screen.getByLabelText('Notes'), '  ');
      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(await patchBodies()).toEqual([{ notes: null }]);
    });
  });

  describe('leaving the form', () => {
    it('goes back after a save when there is history', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      await fireEvent.changeText(screen.getByLabelText('Notes'), 'Hello');

      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(mockRouter.replace).not.toHaveBeenCalled();
    });

    it('on a fresh load, with no history, a save lands on the item itself', async () => {
      mockRouter.canGoBack.mockReturnValue(false);
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');
      await fireEvent.changeText(screen.getByLabelText('Notes'), 'Hello');

      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith(`/item/${MATRIX}`));
      expect(mockRouter.back).not.toHaveBeenCalled();
    });

    it('on a fresh load, with no history, Cancel lands on the item itself', async () => {
      mockRouter.canGoBack.mockReturnValue(false);
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.press(screen.getByLabelText('Cancel'));

      expect(mockRouter.replace).toHaveBeenCalledWith(`/item/${MATRIX}`);
      expect(mockRouter.back).not.toHaveBeenCalled();
      expect(patchHeaders).toEqual([]);
    });

    it('Cancel goes back when there is history', async () => {
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.press(screen.getByLabelText('Cancel'));

      expect(mockRouter.back).toHaveBeenCalled();
      expect(mockRouter.replace).not.toHaveBeenCalled();
    });
  });

  it('shows completeness for games only, never for books or movies (#441)', async () => {
    const game = await renderWithProviders(<ItemEditScreen id={MARIO} />);
    await screen.findByLabelText('Notes');
    expect(screen.getByLabelText('Completeness not set')).toBeTruthy();
    await game.unmount();

    const book = await renderWithProviders(<ItemEditScreen id={DUNE} />);
    await screen.findByLabelText('Notes');
    expect(screen.queryByLabelText('Completeness not set')).toBeNull();
    expect(screen.queryByText('Completeness')).toBeNull();
    await book.unmount();

    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    expect(screen.queryByLabelText('Completeness not set')).toBeNull();
    expect(screen.queryByText('Completeness')).toBeNull();
  });

  it('picks the acquired date from the date picker and keeps it after saving (#441)', async () => {
    const first = await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    expect(within(screen.getByLabelText('Acquired')).getByText('Not set')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Acquired'));
    expect(screen.getByTestId('acquired-picker')).toBeTruthy();
    await act(async () => {
      pickerOnChange()({ type: 'set' }, new Date(2024, 5, 3));
    });
    expect(within(screen.getByLabelText('Acquired')).getByText('2024-06-03')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Save changes'));
    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.acquiredOn).toBe('2024-06-03');
    await first.unmount();

    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    expect(within(screen.getByLabelText('Acquired')).getByText('2024-06-03')).toBeTruthy();
  });

  it('clears the acquired date back to Not set (#441)', async () => {
    entry().item.acquiredOn = '2024-06-02';
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    expect(within(screen.getByLabelText('Acquired')).getByText('2024-06-02')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Clear date'));
    expect(within(screen.getByLabelText('Acquired')).getByText('Not set')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.acquiredOn).toBeNull();
  });

  it('a dismissed picker leaves the acquired date alone (#441)', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Acquired'));
    expect(screen.getByTestId('acquired-picker')).toBeTruthy();
    await act(async () => {
      pickerOnChange()({ type: 'dismissed' }, undefined);
    });

    expect(screen.queryByTestId('acquired-picker')).toBeNull();
    expect(within(screen.getByLabelText('Acquired')).getByText('Not set')).toBeTruthy();
  });

  describe('on the web, where the native picker does not exist', () => {
    const originalOs = Platform.OS;
    afterEach(() => {
      Platform.OS = originalOs;
    });

    it('rejects a date that is not YYYY-MM-DD without calling the server', async () => {
      Platform.OS = 'web';
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.changeText(screen.getByLabelText('Acquired'), 'June 2');
      await fireEvent.press(screen.getByLabelText('Save changes'));

      expect(await screen.findByText('Use the format 2024-06-02.')).toBeTruthy();
      expect(patchHeaders).toEqual([]);
    });

    it('saves a valid typed date', async () => {
      Platform.OS = 'web';
      await renderWithProviders(<ItemEditScreen id={MATRIX} />);
      await screen.findByLabelText('Notes');

      await fireEvent.changeText(screen.getByLabelText('Acquired'), '2024-06-03');
      await fireEvent.press(screen.getByLabelText('Save changes'));

      await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
      expect(entry().item.acquiredOn).toBe('2024-06-03');
    });
  });

  it('asks who an item is lent to when its status is Lent out', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Status Lent out'));
    await fireEvent.changeText(screen.getByLabelText('Lent to'), 'Sam');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.status).toBe('lent');
    expect(entry().item.lentTo).toBe('Sam');
  });

  it('records the local day when an item is lent in the evening', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Status Lent out'));
    await fireEvent.changeText(screen.getByLabelText('Lent to'), 'Sam');

    // 9:30pm Oct 24 in US Central, already Oct 25 in UTC. Only `new Date()` with no
    // arguments is fixed; dated constructions and every static pass straight through.
    // (Timezone comes from the gate's TZ=America/Chicago run; setting process.env.TZ
    // here would be too late.)
    const RealDate = globalThis.Date;
    const evening = RealDate.parse('2026-10-25T02:30:00Z');
    globalThis.Date = new Proxy(RealDate, {
      construct: (target, args) => (args.length === 0 ? new target(evening) : Reflect.construct(target, args)),
    });
    try {
      await fireEvent.press(screen.getByLabelText('Save changes'));
    } finally {
      globalThis.Date = RealDate;
    }

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.lentOn).toBe('2026-10-24');
  });

  it('keeps every text field above the keyboard (#442)', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    // The Lent to field only appears for lent items; show it so the whole
    // form, not only Notes, is covered.
    await fireEvent.press(screen.getByLabelText('Status Lent out'));
    await screen.findByLabelText('Lent to');

    const avoiding = screen.queryByTestId('keyboard-avoiding-view');
    expect(avoiding).not.toBeNull();
    const form = within(avoiding!);
    expect(form.queryByLabelText('Lent to')).not.toBeNull();
    expect(form.queryByLabelText('Acquired')).not.toBeNull();
    expect(form.queryByLabelText('Notes')).not.toBeNull();
  });

  it('gives every button an accessibility label', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    for (const button of screen.getAllByRole('button')) {
      expect(button.props.accessibilityLabel).toBeTruthy();
    }
  });
});

describe('item routes', () => {
  it('keeps the detail screen inside the safe area', async () => {
    await renderWithProviders(<DetailRoute />);
    await screen.findByText('The Matrix');
    expect(screen.getByTestId('item-detail-safe-area')).toBeTruthy();
  });

  it('keeps the edit screen inside the safe area', async () => {
    await renderWithProviders(<EditRoute />);
    await screen.findByLabelText('Notes');
    expect(screen.getByTestId('item-edit-safe-area')).toBeTruthy();
  });
});
