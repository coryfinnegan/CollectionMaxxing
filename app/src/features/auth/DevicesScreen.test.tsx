import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, http } from 'msw';
import { server } from '@/mocks/server';
import { PasskeyCancelledError } from '@/lib/auth/errors';
import { DevicesScreen } from './DevicesScreen';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

beforeEach(() => {
  resetMockAuth();
  mockRouter.replace.mockClear();
  configureAuth({ platform: 'web' });
});
afterEach(() => {
  jest.useRealTimers();
  resetAuth();
});

describe('DevicesScreen', () => {
  it('lists devices with when they were last seen', async () => {
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText('Cory’s iPhone')).toBeTruthy();
    expect(screen.getByText('This browser (Edge on Windows) (this device)')).toBeTruthy();
    expect(screen.getAllByText(/Last seen/).length).toBe(2);
  });

  it('asks before revoking, then signs the device out', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Revoke Cory’s iPhone'));
    expect(screen.getByText('Sign out Cory’s iPhone?')).toBeTruthy();
    expect(mockAuth.sessions).toHaveLength(2); // nothing happens until confirmed

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(screen.queryByText('Cory’s iPhone')).toBeNull());
    expect(mockAuth.sessions).toHaveLength(1);
  });

  it('cancelling the confirmation keeps the device', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Revoke Cory’s iPhone'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText('Sign out Cory’s iPhone?')).toBeNull();
    expect(mockAuth.sessions).toHaveLength(2);
  });

  it('warns before the current device signs itself out', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Revoke This browser (Edge on Windows)'));

    expect(screen.getByText(/the device you're using right now/)).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Sign out this device'));
    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
  });

  it('signs out of every browser after asking, keeps the phones, and goes to the sign-in page', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out of all browsers'));
    expect(screen.getByText('Sign out of all browsers?')).toBeTruthy();
    expect(mockAuth.sessions).toHaveLength(2); // nothing happens until confirmed

    await fireEvent.press(screen.getByLabelText('Sign out all'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(mockAuth.sessions.map((s) => s.kind)).toEqual(['device']);
  });

  it('on the phone, signing out of all browsers stays on Devices, refreshes the list and keeps the phones', async () => {
    configureAuth({ platform: 'native' });
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out of all browsers'));
    await fireEvent.press(screen.getByLabelText('Sign out all'));

    await waitFor(() => expect(screen.queryByText(/This browser/)).toBeNull());
    expect(screen.getByText('Cory’s iPhone')).toBeTruthy();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('shows the devices that loaded and a friendly note when the browsers list fails', async () => {
    server.use(http.get('*/api/v1/me/web-sessions', () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 })));
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText('Cory’s iPhone')).toBeTruthy();
    expect(await screen.findByText("Couldn't load your browsers. Try again in a moment.")).toBeTruthy();
  });

  it('shows the browsers that loaded and a friendly note when the phones list fails', async () => {
    server.use(http.get('*/api/v1/me/sessions', () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 })));
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText('This browser (Edge on Windows) (this device)')).toBeTruthy();
    expect(await screen.findByText("Couldn't load your phones. Try again in a moment.")).toBeTruthy();
  });

  it('shows the friendly error when both lists fail', async () => {
    server.use(
      http.get('*/api/v1/me/sessions', () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 })),
      http.get('*/api/v1/me/web-sessions', () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 })),
    );
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText("That didn't work. Try again.")).toBeTruthy();
  });

  it('cancelling "sign out of all browsers" changes nothing', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out of all browsers'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText('Sign out of all browsers?')).toBeNull();
    expect(mockAuth.sessions).toHaveLength(2);
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('adds a device: shows the QR, the code and a two-minute countdown', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Add a device'));

    expect(await screen.findByText('123456')).toBeTruthy();
    expect(screen.getByTestId('qr-code')).toBeTruthy();
    expect(screen.getByText(/Expires in [12]:\d\d/)).toBeTruthy();
  });

  it('counts down and then says the code expired', async () => {
    jest.useFakeTimers({ doNotFake: ['nextTick', 'setImmediate', 'queueMicrotask'] });
    await renderWithProviders(<DevicesScreen />);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(50);
    });
    await fireEvent.press(screen.getByLabelText('Add a device'));
    await act(async () => {
      await jest.advanceTimersByTimeAsync(50);
    });
    expect(screen.getByText('Expires in 2:00')).toBeTruthy();

    await act(async () => {
      await jest.advanceTimersByTimeAsync(121_000);
    });

    expect(screen.getByText(/This code has expired/)).toBeTruthy();
  });

  it('re-authenticates once when adding a device needs a fresh passkey', async () => {
    mockAuth.requireReauth = true;
    const reauth = jest.fn(async () => {
      mockAuth.requireReauth = false;
    });
    configureAuth({ reauth });
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Add a device'));

    expect(await screen.findByText('123456')).toBeTruthy();
    expect(reauth).toHaveBeenCalledTimes(1);
  });

  it('shows a friendly message when the passkey prompt is cancelled', async () => {
    mockAuth.requireReauth = true;
    configureAuth({ reauth: () => Promise.reject(new PasskeyCancelledError()) });
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Add a device'));

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
    expect(screen.queryByText('123456')).toBeNull();
  });
});
