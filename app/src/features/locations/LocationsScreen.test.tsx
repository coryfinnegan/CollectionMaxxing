import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockData } from '@/mocks/data';
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

    await fireEvent.press(screen.getByLabelText('Add location'));

    expect(await screen.findByText('Give it a name.')).toBeTruthy();
    expect(mockData.locations).toHaveLength(4);
  });

  it('renames a location', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');

    await fireEvent.press(screen.getByLabelText('Rename Living room'));
    await fireEvent.changeText(screen.getByLabelText('Rename Living room'), 'Lounge');
    await fireEvent.press(screen.getByLabelText('Save name'));

    expect(await screen.findByText('Lounge')).toBeTruthy();
    expect(screen.queryByText('Living room')).toBeNull();
  });

  it('deletes a location and removes its row', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');

    await fireEvent.press(screen.getByLabelText('Delete Living room'));

    await waitFor(() => expect(screen.queryByText('Living room')).toBeNull());
    expect(mockData.locations.map((l) => l.name)).not.toContain('Living room');
  });

  it('puts a location back and explains when the server refuses to delete it', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText('Game room');

    await fireEvent.press(screen.getByLabelText('Delete Game room'));

    expect(await screen.findByText('This location still has locations inside it.')).toBeTruthy();
    expect(screen.getByText('Game room')).toBeTruthy();
  });

  it('offers a retry when the list cannot be loaded', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/locations', () => HttpResponse.error()));
    await renderWithProviders(<LocationsScreen />);

    expect(await screen.findByText("Couldn't load your locations")).toBeTruthy();
  });
});
