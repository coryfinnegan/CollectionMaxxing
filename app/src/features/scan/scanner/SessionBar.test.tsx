// The session bar: what this scan inherits, in one line, with the search a tap away; adding by hand and the
// waiting scans beside it, so both stay one tap from the viewfinder.
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import type { SessionContext } from '../session/sessionContext';
import { getTrayStore, setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { SessionBar } from './SessionBar';

const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';

const bar = (
  context: SessionContext,
  actions: { onOpenSession?: jest.Mock; onOpenSearch?: jest.Mock; onAddManually?: jest.Mock; onOpenReview?: jest.Mock } = {},
) => {
  const { onOpenSession = jest.fn(), onOpenSearch = jest.fn(), onAddManually = jest.fn(), onOpenReview = jest.fn() } = actions;
  return renderWithProviders(
    <SessionBar
      libraryId={MOCK_LIBRARY_ID}
      context={context}
      onOpenSession={onOpenSession}
      onOpenSearch={onOpenSearch}
      onAddManually={onAddManually}
      onOpenReview={onOpenReview}
    />,
  ).then(() => ({ onOpenSession, onOpenSearch, onAddManually, onOpenReview }));
};

beforeEach(() => {
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
});
afterEach(() => setTrayStore(undefined));

describe('session bar', () => {
  it('summarizes the session context and opens the sheet on tap', async () => {
    const { onOpenSession } = await bar({ locationId: SHELF_1, status: 'wishlist', mediaHint: 'games', channel: 'home' });

    const summary = await screen.findByLabelText('Session: Game room › Shelf 1, Wishlist, Games, Home. Change settings');
    fireEvent.press(summary);
    expect(onOpenSession).toHaveBeenCalled();
  });

  it('names an explicit Not placed instead of hiding it', async () => {
    await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home' });

    expect(await screen.findByLabelText('Session: Not placed, Owned, Mixed, Home. Change settings')).toBeTruthy();
  });

  it('shows decide later for an in-store scan', async () => {
    await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'in_store' });

    expect(await screen.findByLabelText('Session: Decide later, Owned, Mixed, In-store. Change settings')).toBeTruthy();
  });

  it('opens the search on tap', async () => {
    const { onOpenSearch } = await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home' });

    fireEvent.press(await screen.findByLabelText('Search the catalog'));
    expect(onOpenSearch).toHaveBeenCalled();
  });

  it('adds by hand one tap from the viewfinder', async () => {
    const { onAddManually } = await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home' });

    fireEvent.press(await screen.findByLabelText('Add it by hand'));
    expect(onAddManually).toHaveBeenCalled();
  });

  it('badges the scans waiting from the last session and links them to Review', async () => {
    getTrayStore(MOCK_LIBRARY_ID).getState().scan({ value: '9780441172719', symbology: 'ean-13' });
    getTrayStore(MOCK_LIBRARY_ID).getState().scan({ value: '9780553103540', symbology: 'ean-13' });
    const { onOpenReview } = await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home' });

    const badge = await screen.findByLabelText('Waiting from your last scan: 2 items. Open Review');
    expect(screen.getByText('Waiting: 2 items')).toBeTruthy();
    fireEvent.press(badge);
    expect(onOpenReview).toHaveBeenCalled();
  });

  it('hides the waiting badge when nothing is waiting', async () => {
    await bar({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home' });

    await screen.findByLabelText('Add it by hand');
    expect(screen.queryByText(/waiting/)).toBeNull();
  });
});
