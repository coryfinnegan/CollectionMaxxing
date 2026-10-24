import { fireEvent, screen } from '@testing-library/react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { renderWithProviders } from '@/test/render';
import { ExportScreen } from './ExportScreen';
import { exportPoll, setSharer } from './services';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const deliver = jest.fn(async () => {});

beforeEach(() => {
  resetMockAccount();
  mockRouter.back.mockClear();
  deliver.mockClear();
  exportPoll.intervalMs = 200; // slow enough for the test to see each state
  setSharer({ deliver });
  configureAuth({ platform: 'web' });
});
afterEach(() => resetAuth());

describe('ExportScreen', () => {
  it('walks queued, running and ready, and offers the download only when ready', async () => {
    await renderWithProviders(<ExportScreen />);
    expect(screen.queryByLabelText('Download export')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Start export'));

    expect(await screen.findByText('Your export is waiting to start.')).toBeTruthy();
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.queryByLabelText('Download export')).toBeNull();
    expect(await screen.findByText(/Putting your library into files/)).toBeTruthy();
    expect(screen.queryByLabelText('Download export')).toBeNull();

    await fireEvent.press(await screen.findByLabelText('Download export'));
    expect(deliver).toHaveBeenCalledWith(expect.stringContaining('/download?token='));
  });

  it('says the export failed and lets you start again', async () => {
    mockAccount.exportOutcome = 'failed';
    await renderWithProviders(<ExportScreen />);

    await fireEvent.press(screen.getByLabelText('Start export'));

    expect(await screen.findByText(/The export didn't finish/)).toBeTruthy();
    expect(screen.queryByLabelText('Download export')).toBeNull();
    mockAccount.exportOutcome = 'ready';
    await fireEvent.press(screen.getByLabelText('Start the export again'));
    expect(await screen.findByLabelText('Download export')).toBeTruthy();
    expect(mockAccount.exportStarts).toBe(2);
  });

  it('offers a way back to Me', async () => {
    await renderWithProviders(<ExportScreen />);

    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.back).toHaveBeenCalled();
  });

  it('offers the share sheet on the iPhone', async () => {
    configureAuth({ platform: 'native' });
    await renderWithProviders(<ExportScreen />);
    await fireEvent.press(screen.getByLabelText('Start export'));
    expect(await screen.findByLabelText('Share export')).toBeTruthy();
  });
});
