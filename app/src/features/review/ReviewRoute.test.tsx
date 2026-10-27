// The review route before the library is ready: a loading state, not a bare line.
import { screen } from '@testing-library/react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { getTrayStore, setTrayStore } from '@/features/scan/tray/storage';
import { renderWithProviders } from '@/test/render';
import { boundScanStore } from './boundScan';
import { ReviewScreen } from './ReviewScreen';
import { ReviewRoute } from './ReviewRoute';

const mockRouter = { push: jest.fn(), back: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));
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

describe('review route scan barcode', () => {
  const A = 'aaaaaaaa-0000-4000-8000-000000000000';

  beforeEach(() => {
    setTrayStore(undefined);
    rendered.mockClear();
    mockRouter.push.mockClear();
    boundScanStore.getState().clear();
    library.mockReturnValue({ libraryId: A, isLoading: false });
  });
  afterEach(() => {
    setTrayStore(undefined);
    boundScanStore.getState().clear();
  });

  it('binds the row and opens the scanner for it', async () => {
    getTrayStore(A).getState().setSessionId('session-A');
    await renderWithProviders(<ReviewRoute />);
    const onScanBarcode = rendered.mock.calls[0]?.[0].onScanBarcode;

    onScanBarcode({ id: 'row-1', release: { title: 'Dune' }, visibleText: null, rawCode: null });

    expect(boundScanStore.getState().binding).toEqual({ sessionId: 'session-A', candidateId: 'row-1', title: 'Dune', kind: 'barcode' });
    expect(mockRouter.push).toHaveBeenCalledWith('/scanner');
  });

  it('binds the row for a cover and opens the scanner for it', async () => {
    getTrayStore(A).getState().setSessionId('session-A');
    await renderWithProviders(<ReviewRoute />);
    const onSnapCover = rendered.mock.calls[0]?.[0].onSnapCover;

    onSnapCover({ id: 'row-1', release: { title: 'Dune' }, visibleText: null, rawCode: null });

    expect(boundScanStore.getState().binding).toEqual({ sessionId: 'session-A', candidateId: 'row-1', title: 'Dune', kind: 'cover' });
    expect(mockRouter.push).toHaveBeenCalledWith('/scanner');
  });
});

describe('review route open result', () => {
  const A = 'aaaaaaaa-0000-4000-8000-000000000000';

  beforeEach(() => {
    setTrayStore(undefined);
    rendered.mockClear();
    mockRouter.push.mockClear();
    library.mockReturnValue({ libraryId: A, isLoading: false });
  });
  afterEach(() => setTrayStore(undefined));

  it('opens the tapped row on its Result screen', async () => {
    getTrayStore(A).getState().setSessionId('session-A');
    await renderWithProviders(<ReviewRoute />);
    const onOpenResult = rendered.mock.calls[0]?.[0].onOpenResult;

    onOpenResult({ id: 'row-1' });

    expect(mockRouter.push).toHaveBeenCalledWith({ pathname: '/result/[id]', params: { id: 'row-1' } });
  });
});
