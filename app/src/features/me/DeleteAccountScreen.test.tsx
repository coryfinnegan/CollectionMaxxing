import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { Platform } from 'react-native';
import { ReauthCancelledError } from '@/lib/auth/errors';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { renderWithProviders } from '@/test/render';
import { DeleteAccountScreen, membersMessage } from './DeleteAccountScreen';
import { registerLocalDataClearer } from './localData';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

// The back button only shows on the web now (#303); the phone uses the stack header.
const originalOs = Platform.OS;
afterEach(() => {
  Platform.OS = originalOs;
});

const CONFIRM = 'Delete my account';
let nativeAuth: MemoryNativeAuth;

beforeEach(() => {
  resetMockAccount();
  mockRouter.back.mockClear();
  mockRouter.replace.mockClear();
  resetMockAuth();
  nativeAuth = new MemoryNativeAuth();
  configureAuth({ platform: 'web', native: nativeAuth });
});
afterEach(() => resetAuth());

describe('DeleteAccountScreen', () => {
  it('explains what will happen before anything is deleted', async () => {
    await renderWithProviders(<DeleteAccountScreen />);

    expect(screen.getByText('Your library and photos are deleted within 30 days.')).toBeTruthy();
    expect(screen.getAllByText(/can't be undone/).length).toBeGreaterThan(0);
    expect(mockAccount.deleted).toBe(false);
  });

  it('offers a way back to Me on the web', async () => {
    Platform.OS = 'web';
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.back).toHaveBeenCalled();
  });

  it('deletes nothing when the person declines the fresh sign-in', async () => {
    mockAuth.requireReauth = true;
    configureAuth({ platform: 'web', native: nativeAuth, reauth: () => Promise.reject(new ReauthCancelledError()) });
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText(CONFIRM));

    expect(await screen.findByText('Okay, nothing changed.')).toBeTruthy();
    expect(mockAccount.deleted).toBe(false);
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('keeps you signed in and explains when the library still has members', async () => {
    mockAccount.libraryHasMembers = true;
    const cleared = jest.fn();
    const unregister = registerLocalDataClearer(cleared);
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText(CONFIRM));

    expect(await screen.findByText(membersMessage)).toBeTruthy();
    expect(mockAccount.deleted).toBe(false);
    expect(cleared).not.toHaveBeenCalled();
    expect(nativeAuth.token).not.toBeNull();
    expect(mockRouter.replace).not.toHaveBeenCalled();
    unregister();
  });

  it('clears local data and goes to sign-in once the server accepts the sign-in', async () => {
    const cleared = jest.fn();
    const unregister = registerLocalDataClearer(cleared);
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText(CONFIRM));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(mockAccount.deleted).toBe(true);
    expect(cleared).toHaveBeenCalled();
    expect(nativeAuth.token).toBeNull();
    unregister();
  });

  it('on the iPhone, explains where to delete the account instead of offering the button', async () => {
    configureAuth({ platform: 'native' });
    await renderWithProviders(<DeleteAccountScreen />);

    expect(screen.queryByLabelText(CONFIRM)).toBeNull();
    expect(screen.getByText(/open CollectionMaxxing in a browser on your computer/)).toBeTruthy();
  });
});
