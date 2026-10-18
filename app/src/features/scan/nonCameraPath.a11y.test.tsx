// The camera is never the only way in: Import from Photos sits on the scanner (which works with no camera picture),
// and the Scan tab offers adding by hand, which lands on the library's Add an item panel.
import { fireEvent, screen } from '@testing-library/react-native';
import { renderWithProviders } from '@/test/render';
import { makeKit, resetKit } from './pipeline/testKit';
import { importKit } from './import/importKit';
import { ScanTab } from './ScanTab';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

afterEach(() => {
  resetKit();
  mockRouter.push.mockClear();
});

describe('using the app without the camera', () => {
  it('keeps Import from Photos on the scanner even though no camera picture is shown', async () => {
    // importKit opens the scanner with no `preview` element: nothing from the camera is on screen.
    await importKit(makeKit()).open();
    const photos = screen.getByRole('button', { name: 'Photos' });
    expect(photos.props.accessibilityHint).toBe('Choose pictures from your library to read, with no camera needed');
  });

  it('offers adding an item by hand from the Scan tab', async () => {
    await renderWithProviders(<ScanTab />);
    fireEvent.press(screen.getByRole('button', { name: 'Add an item by hand' }));
    expect(mockRouter.push).toHaveBeenCalledWith({ pathname: '/library', params: { add: '1' } });
  });
});
