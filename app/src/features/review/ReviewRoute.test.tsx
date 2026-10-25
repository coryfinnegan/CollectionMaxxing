// The review route before the library is ready: a loading state, not a bare line.
import { screen } from '@testing-library/react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { getTrayStore, setTrayStore } from '@/features/scan/tray/storage';
import { renderWithProviders } from '@/test/render';
import { ReviewScreen } from './ReviewScreen';
import { ReviewRoute } from './ReviewRoute';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn() }) }));
jest.mock('@/features/locations/useLocations', () => ({ useActiveLibraryId: jest.fn() }));
// The pairing tests only need which session the route hands the screen; stub the screen and read its props.
jest.mock('./ReviewScreen', () => ({ ReviewScreen: jest.fn(() => null) }));
const library = useActiveLibraryId as jest.Mock;
const rendered = ReviewScreen as unknown as jest.Mock;

describe('review route without a library', () => {
  it('shows a loading state while the library loads', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: true });
    await renderWithProviders(<ReviewRoute />);
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Loading…')).toBeTruthy();
  });
});

describe('review route library pairing', () => {
  const A = 'aaaaaaaa-0000-4000-8000-000000000000';
  const B = 'bbbbbbbb-0000-4000-8000-000000000000';

  beforeEach(() => {
    setTrayStore(undefined);
    rendered.mockClear();
  });
  afterEach(() => setTrayStore(undefined));

  it('pairs the current library with its own stored session', async () => {
    getTrayStore(A).getState().setSessionId('session-A');
    library.mockReturnValue({ libraryId: A, isLoading: false });

    await renderWithProviders(<ReviewRoute />);

    expect(rendered.mock.calls[0]?.[0]).toMatchObject({ libraryId: A, sessionId: 'session-A' });
  });

  it('never pairs the current library with another library’s stored session', async () => {
    getTrayStore(A).getState().setSessionId('session-A');
    library.mockReturnValue({ libraryId: B, isLoading: false });

    await renderWithProviders(<ReviewRoute />);

    expect(rendered.mock.calls[0]?.[0]).toMatchObject({ libraryId: B, sessionId: null });
  });
});
