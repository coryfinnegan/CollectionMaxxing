import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { renderWithProviders } from '@/test/render';
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
