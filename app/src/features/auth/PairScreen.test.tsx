import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { http, HttpResponse } from 'msw';
import { resetAuth, getAccessToken, getTokenStore } from '@/lib/auth/session';
import { MOCK_PAIR_CODE, MOCK_PAIR_TOKEN } from '@/mocks/auth';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { PairScreen } from './PairScreen';
import { FakeQrScanner, UnavailableQrScanner } from './qr';
import { setQrScanner } from './services';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SERVER = 'https://pc.tailnet.ts.net';
let logged: string[];

beforeEach(() => {
  mockRouter.replace.mockClear();
  logged = [];
  for (const level of ['log', 'info', 'warn', 'error'] as const) {
    jest.spyOn(console, level).mockImplementation((...args: unknown[]) => {
      logged.push(args.map(String).join(' '));
    });
  }
});
afterEach(() => {
  jest.restoreAllMocks();
  resetAuth();
});

describe('PairScreen', () => {
  it('pairs from a scanned payload, keeps the refresh token in the store and opens the library', async () => {
    setQrScanner(new FakeQrScanner(JSON.stringify({ serverUrl: SERVER, token: MOCK_PAIR_TOKEN })));
    await renderWithProviders(<PairScreen />);

    await fireEvent.press(screen.getByLabelText('Scan QR code'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(await getTokenStore().load()).toEqual({ refreshToken: 'mock-refresh', serverUrl: SERVER });
    expect(getAccessToken()).toBe('mock-access');
  });

  it('sends the scanned token to the token endpoint', async () => {
    let form: URLSearchParams | undefined;
    server.use(
      http.post('*/connect/token', async ({ request }) => {
        form = new URLSearchParams(await request.text());
        return HttpResponse.json({ access_token: 'a', refresh_token: 'r' });
      }),
    );
    setQrScanner(new FakeQrScanner(JSON.stringify({ serverUrl: SERVER, token: MOCK_PAIR_TOKEN })));
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Scan QR code'));

    await waitFor(() => expect(form).toBeDefined());
    expect(form?.get('grant_type')).toBe('urn:cm:device_pairing');
    expect(form?.get('pairing_token')).toBe(MOCK_PAIR_TOKEN);
  });

  it('pairs from a typed 6-digit code and server address', async () => {
    await renderWithProviders(<PairScreen />);

    await fireEvent.press(screen.getByLabelText('Enter a 6-digit code'));
    await fireEvent.changeText(screen.getByLabelText('Server address'), 'pc.tailnet.ts.net');
    await fireEvent.changeText(screen.getByLabelText('6-digit code'), MOCK_PAIR_CODE);
    await fireEvent.press(screen.getByLabelText('Pair this iPhone'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect((await getTokenStore().load())?.serverUrl).toBe(SERVER);
  });

  it('never logs the pairing code or token', async () => {
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Enter a 6-digit code'));
    await fireEvent.changeText(screen.getByLabelText('Server address'), 'pc.tailnet.ts.net');
    await fireEvent.changeText(screen.getByLabelText('6-digit code'), '999999');
    await fireEvent.press(screen.getByLabelText('Pair this iPhone'));
    await screen.findByText(/That code didn't work/);

    setQrScanner(new FakeQrScanner(JSON.stringify({ serverUrl: SERVER, token: MOCK_PAIR_TOKEN })));
    await fireEvent.press(screen.getByLabelText('Scan QR code'));
    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalled());

    expect(logged.length).toBeGreaterThan(0); // the logger did speak ...
    expect(logged.join('\n')).not.toContain('999999');
    expect(logged.join('\n')).not.toContain(MOCK_PAIR_TOKEN);
  });

  it('shows a friendly message for a wrong code and does not store anything', async () => {
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Enter a 6-digit code'));
    await fireEvent.changeText(screen.getByLabelText('Server address'), 'pc.tailnet.ts.net');
    await fireEvent.changeText(screen.getByLabelText('6-digit code'), '000000');
    await fireEvent.press(screen.getByLabelText('Pair this iPhone'));

    expect(await screen.findByText(/That code didn't work/)).toBeTruthy();
    expect(await getTokenStore().load()).toBeNull();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('rejects a QR code that is not ours', async () => {
    setQrScanner(new FakeQrScanner('https://example.com/not-ours'));
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Scan QR code'));

    expect(await screen.findByText(/isn't from CollectionMaxxing/)).toBeTruthy();
  });

  it('stays put when the person backs out of the scanner', async () => {
    setQrScanner(new FakeQrScanner(null));
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Scan QR code'));

    await waitFor(() => expect(screen.getByLabelText('Scan QR code').props.accessibilityState.disabled).toBe(false));
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('falls back to typing a code while the camera scanner is not wired', async () => {
    setQrScanner(new UnavailableQrScanner());
    await renderWithProviders(<PairScreen />);
    await fireEvent.press(screen.getByLabelText('Scan QR code'));

    expect(await screen.findByText(/Enter the 6-digit code instead/)).toBeTruthy();
    expect(screen.getByLabelText('6-digit code')).toBeTruthy();
  });
});
