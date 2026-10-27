// The scanner's one control row (#408): the session chip plus Flash, Search, Photos and manual add as compact
// icon controls in a single fixed row. Every control stays one tap away, with its spoken label unchanged.
import { fireEvent, screen } from '@testing-library/react-native';
import { Platform } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import type { SessionContext } from '../session/sessionContext';
import { ScannerControls } from './ScannerControls';

const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';

const controls = (
  context: SessionContext,
  actions: {
    onClose?: jest.Mock;
    onOpenSession?: jest.Mock;
    onToggleFlash?: jest.Mock;
    onOpenSearch?: jest.Mock;
    onPhotos?: jest.Mock;
    onAddManually?: jest.Mock;
  } = {},
  options: { showClose?: boolean; flash?: boolean; photosEnabled?: boolean; photosRunning?: boolean } = {},
) => {
  const { onClose = jest.fn(), onOpenSession = jest.fn(), onToggleFlash = jest.fn(), onOpenSearch = jest.fn(), onPhotos = jest.fn(), onAddManually = jest.fn() } = actions;
  return renderWithProviders(
    <ScannerControls
      libraryId={MOCK_LIBRARY_ID}
      context={context}
      showClose={options.showClose ?? false}
      onClose={onClose}
      onOpenSession={onOpenSession}
      flash={options.flash ?? false}
      onToggleFlash={onToggleFlash}
      onOpenSearch={onOpenSearch}
      photosEnabled={options.photosEnabled ?? false}
      photosRunning={options.photosRunning ?? false}
      onPhotos={onPhotos}
      onAddManually={onAddManually}
    />,
  ).then(() => ({ onClose, onOpenSession, onToggleFlash, onOpenSearch, onPhotos, onAddManually }));
};

const home = { locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home', autoAddConfident: false } as SessionContext;

const originalOs = Platform.OS;
afterEach(() => {
  Platform.OS = originalOs;
});

describe('scanner controls', () => {
  it('lays every control out in one row', async () => {
    await controls(home);

    expect(screen.getByTestId('scanner-controls').props.style).toEqual(expect.objectContaining({ flexDirection: 'row' }));
  });

  it('summarizes the session context in one tappable chip and opens the sheet on tap', async () => {
    const { onOpenSession } = await controls({ locationId: SHELF_1, status: 'wishlist', mediaHint: 'games', channel: 'home', autoAddConfident: false });

    const chip = await screen.findByLabelText('Session: Game room › Shelf 1, Wishlist, Games, Home. Change settings');
    await fireEvent.press(chip);
    expect(onOpenSession).toHaveBeenCalled();
  });

  it('names an explicit Not placed instead of hiding it', async () => {
    await controls(home);

    expect(await screen.findByLabelText('Session: Not placed, Owned, Mixed, Home. Change settings')).toBeTruthy();
  });

  it('shows decide later for an in-store scan', async () => {
    await controls({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'in_store', autoAddConfident: false });

    expect(await screen.findByLabelText('Session: Decide later, Owned, Mixed, In-store. Change settings')).toBeTruthy();
  });

  it('toggles the flash from its icon', async () => {
    const { onToggleFlash } = await controls(home);

    const flash = await screen.findByLabelText('Flash');
    await fireEvent.press(flash);
    expect(onToggleFlash).toHaveBeenCalled();
  });

  it('says when the flash is on', async () => {
    await controls(home, {}, { flash: true });

    const flash = await screen.findByLabelText('Flash on');
    expect(flash.props.accessibilityState).toEqual(expect.objectContaining({ selected: true }));
  });

  it('opens the search on tap', async () => {
    const { onOpenSearch } = await controls(home);

    await fireEvent.press(await screen.findByLabelText('Search the catalog'));
    expect(onOpenSearch).toHaveBeenCalled();
  });

  it('shows Photos while the import runs, and starts it on tap', async () => {
    const { onPhotos } = await controls(home, {}, { photosEnabled: true });

    const photos = await screen.findByLabelText('Photos');
    expect(photos.props.accessibilityHint).toBe('Choose pictures from your library to read, with no camera needed');
    await fireEvent.press(photos);
    expect(onPhotos).toHaveBeenCalled();
  });

  it('hides Photos where there is no picture pipeline', async () => {
    await controls(home, {}, { photosEnabled: false });

    await screen.findByLabelText('Search the catalog');
    expect(screen.queryByLabelText('Photos')).toBeNull();
  });

  it('disables Photos while an import runs', async () => {
    await controls(home, {}, { photosEnabled: true, photosRunning: true });

    const photos = await screen.findByLabelText('Photos');
    expect(photos.props.accessibilityState).toEqual(expect.objectContaining({ disabled: true }));
  });

  it('adds by hand one tap from the viewfinder', async () => {
    const { onAddManually } = await controls(home);

    await fireEvent.press(await screen.findByLabelText('Add it by hand'));
    expect(onAddManually).toHaveBeenCalled();
  });

  it('closes where Close is offered', async () => {
    const { onClose } = await controls(home, {}, { showClose: true });

    await fireEvent.press(await screen.findByLabelText('Close'));
    expect(onClose).toHaveBeenCalled();
  });

  it('reads its controls in viewfinder order', async () => {
    await controls(home, {}, { showClose: true, photosEnabled: true });

    await screen.findByLabelText('Add it by hand');
    expect(screen.getAllByRole('button').map((b) => b.props.accessibilityLabel)).toEqual([
      'Close',
      'Session: Not placed, Owned, Mixed, Home. Change settings',
      'Flash',
      'Search the catalog',
      'Photos',
      'Add it by hand',
    ]);
  });

  it('names the scanner for screen readers on the web', async () => {
    Platform.OS = 'web';
    await controls(home);

    expect(await screen.findByRole('header', { name: 'Scanner' })).toBeTruthy();
  });
});
