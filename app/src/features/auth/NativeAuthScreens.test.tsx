import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { MemoryKeyValueStore, createTrayStore } from '@/features/scan/tray/trayStore';
import { setTrayStore } from '@/features/scan/tray/storage';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { renderWithProviders } from '@/test/render';
import { SignInScreen, signInFailedCopy } from './NativeAuthScreens';

const mockRegister = jest.fn();
jest.mock('./appSignIn', () => ({ registerThisInstall: () => mockRegister() }));
const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

let native: MemoryNativeAuth;
beforeEach(() => {
  mockRegister.mockReset().mockResolvedValue(undefined);
  mockRouter.replace.mockClear();
  native = new MemoryNativeAuth(null);
  configureAuth({ platform: 'native', native });
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
});
afterEach(() => {
  resetAuth();
  setTrayStore(undefined);
});

describe('Sign in (iPhone)', () => {
  it('signs in, registers this install, then opens the library', async () => {
    await renderWithProviders(<SignInScreen />);

    await fireEvent.press(screen.getByLabelText('Sign in'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(native.token).not.toBeNull();
    expect(mockRegister).toHaveBeenCalledTimes(1);
  });

  it('backing out of the sheet explains and registers nothing', async () => {
    native.signIn = jest.fn().mockRejectedValue(new Error('user_cancelled'));
    await renderWithProviders(<SignInScreen />);

    await fireEvent.press(screen.getByLabelText('Sign in'));

    expect(await screen.findByText(signInFailedCopy)).toBeTruthy();
    expect(mockRegister).not.toHaveBeenCalled();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });
});
