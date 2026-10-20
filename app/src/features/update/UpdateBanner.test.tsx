import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { AppState, type AppStateStatus } from 'react-native';
import { HttpResponse, http } from 'msw';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { server } from '@/mocks/server';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { renderWithProviders } from '@/test/render';
import { setUpdateEnvironment, resetUpdateEnvironment } from './services';
import { UpdateBanner } from './UpdateBanner';

const DAY = 24 * 60 * 60 * 1000;
const READY = 'Version 1.0.2 is ready. Install it at home on your Wi-Fi.';

let store: MemoryKeyValueStore;
let clock: number;
let opened: string[];
let infoRequests: number;
let serverVersion: string | null;
let foreground: ((state: AppStateStatus) => void) | undefined;

const info = () =>
  http.get('*/api/v1/app/ipa/info', () => {
    infoRequests++;
    return HttpResponse.json(
      serverVersion
        ? { available: true, sizeBytes: 5, modifiedAt: '2026-10-20T12:00:00Z', sha256: 'a'.repeat(64), version: serverVersion }
        : { available: false, sizeBytes: null, modifiedAt: null, sha256: null, version: null },
    );
  });

beforeEach(() => {
  store = new MemoryKeyValueStore();
  clock = Date.parse('2026-10-20T12:00:00Z');
  opened = [];
  infoRequests = 0;
  serverVersion = '1.0.2';
  foreground = undefined;
  configureAuth({ platform: 'native' });
  setUpdateEnvironment({ store, now: () => clock, appVersion: () => '1.0.1', open: async (url) => void opened.push(url) });
  server.use(info());
  jest.spyOn(AppState, 'addEventListener').mockImplementation((_type, listener) => {
    foreground = listener as (state: AppStateStatus) => void;
    return { remove: jest.fn() };
  });
});
afterEach(() => {
  jest.restoreAllMocks();
  resetUpdateEnvironment();
  resetAuth();
});

const comeToForeground = async () => {
  await act(async () => foreground?.('active'));
};

describe('the update notice', () => {
  it('tells the owner a newer version is ready, in the calm way', async () => {
    await renderWithProviders(<UpdateBanner />);

    expect(await screen.findByText(READY)).toBeTruthy();
    expect(screen.getByLabelText('Download version 1.0.2')).toBeTruthy();
    expect(screen.getByLabelText('Dismiss')).toBeTruthy();
  });

  it('opens the server download in Safari from the button', async () => {
    await renderWithProviders(<UpdateBanner />);

    await fireEvent.press(await screen.findByLabelText('Download version 1.0.2'));

    expect(opened).toEqual(['http://api.test/api/v1/app/ipa']);
  });

  it.each([
    ['1.0.1', '1.0.1'],
    ['1.0.2', '1.0.2'],
    ['1.1.0', '1.2.0'],
  ])('shows nothing when the server has %s and this app (%s) is already as new or newer', async (served, installed) => {
    serverVersion = served;
    setUpdateEnvironment({ appVersion: () => installed });

    await renderWithProviders(<UpdateBanner />);
    await waitFor(() => expect(infoRequests).toBe(1));

    expect(screen.queryByText(/is ready/)).toBeNull();
  });

  it('shows nothing when the server has no .ipa or does not know its version', async () => {
    serverVersion = null;

    await renderWithProviders(<UpdateBanner />);
    await waitFor(() => expect(infoRequests).toBe(1));

    expect(screen.queryByText(/is ready/)).toBeNull();
  });

  it('shows nothing on the web, which is always current, and does not even ask', async () => {
    configureAuth({ platform: 'web' });

    await renderWithProviders(<UpdateBanner />);

    expect(screen.queryByText(/is ready/)).toBeNull();
    expect(infoRequests).toBe(0);
  });

  it('stays quiet when the server cannot be reached', async () => {
    server.use(http.get('*/api/v1/app/ipa/info', () => HttpResponse.error()));
    const errors = jest.spyOn(console, 'error').mockImplementation(() => {});

    await renderWithProviders(<UpdateBanner />);
    await act(async () => {});

    expect(screen.queryByText(/is ready/)).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(errors).not.toHaveBeenCalled();
  });

  it('stays quiet when the server answers with an error', async () => {
    server.use(http.get('*/api/v1/app/ipa/info', () => HttpResponse.json({ title: 'Server error', status: 500 }, { status: 500 })));

    await renderWithProviders(<UpdateBanner />);
    await act(async () => {});

    expect(screen.queryByText(/is ready/)).toBeNull();
  });
});

describe('how often it asks', () => {
  it('asks once, then uses what it remembers for a day', async () => {
    const first = await renderWithProviders(<UpdateBanner />);
    await screen.findByText(READY);
    await first.unmount();

    clock += DAY - 1000;
    await renderWithProviders(<UpdateBanner />);

    expect(await screen.findByText(READY)).toBeTruthy(); // from what it remembers, with no request
    expect(infoRequests).toBe(1);
  });

  it('does not ask again each time the app comes to the foreground within the day', async () => {
    await renderWithProviders(<UpdateBanner />);
    await screen.findByText(READY);

    clock += 60 * 60 * 1000;
    await comeToForeground();
    await comeToForeground();

    expect(infoRequests).toBe(1);
  });

  it('asks again once a day has passed, when the app comes to the foreground', async () => {
    serverVersion = null;
    await renderWithProviders(<UpdateBanner />);
    await waitFor(() => expect(infoRequests).toBe(1));
    expect(screen.queryByText(/is ready/)).toBeNull();

    serverVersion = '1.0.2';
    clock += DAY + 1000;
    await comeToForeground();

    expect(await screen.findByText(READY)).toBeTruthy();
    expect(infoRequests).toBe(2);
  });

  it('ignores the app going to the background', async () => {
    await renderWithProviders(<UpdateBanner />);
    await screen.findByText(READY);
    clock += 2 * DAY;

    await act(async () => foreground?.('background'));

    expect(infoRequests).toBe(1);
  });

  it('tries again at the next foreground when the last try could not reach the server', async () => {
    server.use(http.get('*/api/v1/app/ipa/info', () => HttpResponse.error()));
    await renderWithProviders(<UpdateBanner />);
    await act(async () => {});
    server.resetHandlers();
    server.use(info());

    await comeToForeground();

    expect(await screen.findByText(READY)).toBeTruthy();
  });

  it('keeps showing the remembered notice while offline, even after the day is up', async () => {
    await renderWithProviders(<UpdateBanner />);
    await screen.findByText(READY);

    server.use(http.get('*/api/v1/app/ipa/info', () => HttpResponse.error()));
    clock += 3 * DAY;
    await comeToForeground();

    expect(screen.getByText(READY)).toBeTruthy();
  });

  it('drops the notice once the server no longer has a newer version', async () => {
    await renderWithProviders(<UpdateBanner />);
    await screen.findByText(READY);

    serverVersion = null;
    clock += DAY + 1000;
    await comeToForeground();

    await waitFor(() => expect(screen.queryByText(READY)).toBeNull());
  });
});

describe('dismissing', () => {
  it('hides the notice and keeps it hidden for that version', async () => {
    const first = await renderWithProviders(<UpdateBanner />);
    await fireEvent.press(await screen.findByLabelText('Dismiss'));

    expect(screen.queryByText(READY)).toBeNull();
    await first.unmount();

    clock += 3 * DAY;
    await renderWithProviders(<UpdateBanner />);
    await waitFor(() => expect(infoRequests).toBe(2));
    expect(screen.queryByText(READY)).toBeNull();
  });

  it('does not open the download', async () => {
    await renderWithProviders(<UpdateBanner />);

    await fireEvent.press(await screen.findByLabelText('Dismiss'));

    expect(opened).toEqual([]);
  });

  it('speaks up again for the next version', async () => {
    const first = await renderWithProviders(<UpdateBanner />);
    await fireEvent.press(await screen.findByLabelText('Dismiss'));
    await first.unmount();

    serverVersion = '1.0.3';
    clock += DAY + 1000;
    await renderWithProviders(<UpdateBanner />);

    expect(await screen.findByText('Version 1.0.3 is ready. Install it at home on your Wi-Fi.')).toBeTruthy();
  });

  it('stays quiet after installing, since the installed version catches up with the dismissed one', async () => {
    const first = await renderWithProviders(<UpdateBanner />);
    await fireEvent.press(await screen.findByLabelText('Dismiss'));
    await first.unmount();

    setUpdateEnvironment({ appVersion: () => '1.0.2' });
    clock += DAY + 1000;
    await renderWithProviders(<UpdateBanner />);
    await waitFor(() => expect(infoRequests).toBe(2));

    expect(screen.queryByText(/is ready/)).toBeNull();
  });
});
