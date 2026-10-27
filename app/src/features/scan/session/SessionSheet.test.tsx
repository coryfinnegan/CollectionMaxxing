// The session sheet: where this scan's items go, their status, what is being scanned and where.
// Every capture inherits it; saving a home choice remembers it for the next session.
import { act, fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID, mockAccount, resetMockData } from '@/mocks/data';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { defaultSessionContext, loadSessionMemory, rememberSessionContext, setSessionContextStorage, type SessionContext } from './sessionContext';
import { SessionSheet } from './SessionSheet';

const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';
const LIB_B = 'bbbbbbbb-0000-4000-8000-000000000002';

const open = async (context?: SessionContext) => {
  const saved: { libraryId: string; context: SessionContext }[] = [];
  await renderWithProviders(
    <SessionSheet
      libraryId={MOCK_LIBRARY_ID}
      context={context ?? defaultSessionContext(MOCK_LIBRARY_ID)}
      onSave={(libraryId, next) => saved.push({ libraryId, context: next })}
      onClose={() => undefined}
    />,
  );
  return saved;
};

/** One tap, flushed before the next (state updates inside a sheet need their own act). */
const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

beforeEach(() => {
  resetMockData();
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  setSessionContextStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  setSessionContextStorage(undefined);
});

describe('session sheet', () => {
  it('shows the remembered spot, status, media and channel', async () => {
    rememberSessionContext(MOCK_LIBRARY_ID, { locationId: SHELF_1, status: 'wishlist', mediaHint: 'games', channel: 'home' });
    await open(defaultSessionContext(MOCK_LIBRARY_ID));

    expect(await screen.findByLabelText('Location: Game room › Shelf 1. Change')).toBeTruthy();
    expect(screen.getByLabelText('Wishlist').props.accessibilityState.selected).toBe(true);
    expect(screen.getByLabelText('Mixed').props.accessibilityState.selected).toBe(true);
    expect(screen.getByLabelText('Home').props.accessibilityState.selected).toBe(true);
  });

  it('saving a home choice remembers it and passes it on', async () => {
    const saved = await open();

    await press('Location: Not placed. Change');
    await press('Shelf 1');
    await press('Wishlist');
    await press('Games');
    await press('Save');

    expect(saved).toEqual([
      { libraryId: MOCK_LIBRARY_ID, context: { locationId: SHELF_1, status: 'wishlist', mediaHint: 'games', channel: 'home' } },
    ]);
    expect(loadSessionMemory(MOCK_LIBRARY_ID)).toEqual({ locationId: SHELF_1, status: 'wishlist' });
  });

  it('saving in-store defers the location and leaves the last home spot alone', async () => {
    rememberSessionContext(MOCK_LIBRARY_ID, { locationId: SHELF_1, status: 'owned', mediaHint: 'mixed', channel: 'home' });
    const saved = await open(defaultSessionContext(MOCK_LIBRARY_ID));

    await press('In-store');
    expect(await screen.findByLabelText('Location: decide later')).toBeTruthy();
    await press('Save');

    expect(saved).toEqual([
      { libraryId: MOCK_LIBRARY_ID, context: { locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'in_store' } },
    ]);
    expect(loadSessionMemory(MOCK_LIBRARY_ID)).toEqual({ locationId: SHELF_1, status: 'owned' });
  });

  it('offers no auto-add: every scan waits for Review (#452)', async () => {
    await open();

    expect(screen.queryByLabelText('Auto-add confident matches')).toBeNull();
    expect(screen.queryByText('Auto-add')).toBeNull();
  });

  it('switching library loads that library\u2019s remembered spot', async () => {
    mockAccount.libraries.push({ id: LIB_B, name: "Sam's library", role: 'viewer' });
    rememberSessionContext(LIB_B, { locationId: null, status: 'wishlist', mediaHint: 'mixed', channel: 'home' });
    const saved = await open();

    await press(`Library: Cory's library. Change`);
    await press(`Sam's library, Viewer`);
    expect(await screen.findByLabelText('Location: Not placed. Change')).toBeTruthy();
    expect(screen.getByLabelText('Wishlist').props.accessibilityState.selected).toBe(true);

    await press('Save');
    expect(saved).toEqual([
      { libraryId: LIB_B, context: { locationId: null, status: 'wishlist', mediaHint: 'mixed', channel: 'home' } },
    ]);
    expect(useLibraryStore.getState().activeLibraryId).toBe(LIB_B);
  });
});
