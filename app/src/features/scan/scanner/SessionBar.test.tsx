// The session bar: what this scan inherits, in one line, with the search a tap away.
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import type { SessionContext } from '../session/sessionContext';
import { SessionBar } from './SessionBar';

const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';

const bar = (context: SessionContext, onOpenSession = jest.fn(), onOpenSearch = jest.fn()) =>
  renderWithProviders(
    <SessionBar libraryId={MOCK_LIBRARY_ID} context={context} onOpenSession={onOpenSession} onOpenSearch={onOpenSearch} />,
  ).then(() => ({ onOpenSession, onOpenSearch }));

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
});
