import { act, fireEvent, screen, waitFor, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockData, mockMe } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { LocationsScreen } from './LocationsScreen';

describe('LocationsScreen', () => {
  it('lists locations as a tree, children after their parent', async () => {
    await renderWithProviders(<LocationsScreen />);

    expect(await screen.findByText('Game room')).toBeTruthy();
    expect(screen.getByText('Shelf 1')).toBeTruthy();
    expect(screen.getByText('Living room')).toBeTruthy();
  });

  it('adds a location inside another and shows it straight away', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.press(screen.getByLabelText('Add inside Living room'));
    expect(screen.getByText('Adding inside Living room')).toBeTruthy();
    await fireEvent.changeText(screen.getByLabelText('New location'), 'TV stand');
    await fireEvent.press(screen.getByLabelText('Add location'));

    expect(await screen.findByText('TV stand')).toBeTruthy();
    await waitFor(() => expect(mockData.locations.some((l) => l.name === 'TV stand' && l.parentId)).toBe(true));
  });

  it('shows a friendly message when the server rejects a blank name', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.press(screen.getByLabelText('Add location'));

    expect(await screen.findByText('Give it a name.')).toBeTruthy();
    expect(mockData.locations).toHaveLength(4);
  });

  it('renames a location', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.press(screen.getByLabelText('Rename Living room'));
    await fireEvent.changeText(screen.getByLabelText('Rename Living room'), 'Lounge');
    await fireEvent.press(screen.getByLabelText('Save name'));

    expect(await screen.findByText('Lounge')).toBeTruthy();
    expect(screen.queryByText('Living room')).toBeNull();
  });

  it('deletes a location and removes its row', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.press(screen.getByLabelText('Delete Living room'));

    await waitFor(() => expect(screen.queryByText('Living room')).toBeNull());
    expect(mockData.locations.map((l) => l.name)).not.toContain('Living room');
  });

  it('puts a location back and explains when the server refuses to delete it', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.press(screen.getByLabelText('Delete Game room'));

    expect(await screen.findByText('This location still has locations inside it.')).toBeTruthy();
    expect(screen.getByText('Game room')).toBeTruthy();
  });

  it('disables actions on a new row until the server confirms it', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('*/api/v1/libraries/:libraryId/locations', async () => {
        await gate;
        return HttpResponse.json({ id: 'new-location-id', parentId: null, name: 'TV stand', sortOrder: 4 }, { status: 201 });
      }),
    );
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    await fireEvent.changeText(screen.getByLabelText('New location'), 'TV stand');
    await fireEvent.press(screen.getByLabelText('Add location'));

    // The row shows straight away from the optimistic update, but its id only exists on this device.
    expect(await screen.findByText('TV stand')).toBeTruthy();
    for (const label of ['Add inside TV stand', 'Rename TV stand', 'Delete TV stand']) {
      expect(screen.getByLabelText(label).props.accessibilityState.disabled).toBe(true);
    }
    expect(screen.getByLabelText('Rename Game room').props.accessibilityState.disabled).not.toBe(true);

    await act(async () => {
      release();
    });
    await waitFor(() => expect(screen.queryByText('TV stand')).toBeNull());
  });

  it('offers a retry when the list cannot be loaded', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/locations', () => HttpResponse.error()));
    await renderWithProviders(<LocationsScreen />);

    expect(await screen.findByText("Couldn't load your locations")).toBeTruthy();
  });

  it('keeps the new location field above the keyboard', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');
    await screen.findByLabelText('Add location'); // the editing controls wait for the role

    const avoiding = within(screen.getByTestId('keyboard-avoiding-view'));
    expect(avoiding.getByLabelText('New location')).toBeTruthy();
  });

  describe('for each role', () => {
    const asRole = (role: 'owner' | 'editor' | 'viewer') =>
      server.use(http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), memberships: [{ ...mockMe().memberships[0]!, role }] })));

    it.each(['owner', 'editor'] as const)('lets an %s add, rename and delete', async (role) => {
      asRole(role);
      await renderWithProviders(<LocationsScreen />);

      expect(await screen.findByLabelText('Add location')).toBeTruthy();
      expect(screen.getByLabelText('New location')).toBeTruthy();
      expect(screen.getByLabelText('Add inside Living room')).toBeTruthy();
      expect(screen.getByLabelText('Rename Living room')).toBeTruthy();
      expect(screen.getByLabelText('Delete Living room')).toBeTruthy();
    });

    it('shows a viewer the tree and nothing to change', async () => {
      asRole('viewer');
      let meAnswered = false;
      server.events.on('response:mocked', ({ request }) => {
        if (new URL(request.url).pathname.endsWith('/api/v1/me')) meAnswered = true;
      });
      await renderWithProviders(<LocationsScreen />);

      expect(await screen.findByText('Game room')).toBeTruthy();
      expect(screen.getByText('Living room')).toBeTruthy();
      await waitFor(() => expect(meAnswered).toBe(true));
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
      });

      expect(screen.queryByLabelText('New location')).toBeNull();
      expect(screen.queryByLabelText('Add location')).toBeNull();
      expect(screen.queryByLabelText('Add inside Living room')).toBeNull();
      expect(screen.queryByLabelText('Rename Living room')).toBeNull();
      expect(screen.queryByLabelText('Delete Living room')).toBeNull();
      server.events.removeAllListeners();
    });

    it('keeps the controls hidden until the role is known', async () => {
      server.use(http.get('*/api/v1/me', () => new Promise<never>(() => undefined)));
      await renderWithProviders(<LocationsScreen />);

      expect(await screen.findByText('Game room')).toBeTruthy();
      expect(screen.queryByLabelText('Add location')).toBeNull();
      expect(screen.queryByLabelText('Delete Living room')).toBeNull();
    });
  });
});
