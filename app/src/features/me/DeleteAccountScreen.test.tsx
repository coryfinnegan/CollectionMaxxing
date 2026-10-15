import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { FakePasskeyClient } from '@/features/auth/passkeys';
import { setPasskeys } from '@/features/auth/services';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryTokenStore } from '@/lib/auth/tokenStore';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { renderWithProviders } from '@/test/render';
import { DeleteAccountScreen, membersMessage } from './DeleteAccountScreen';
import { registerLocalDataClearer } from './localData';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const CONFIRM = 'Confirm with passkey and delete account';
const passkeys = new FakePasskeyClient();
let tokenStore: MemoryTokenStore;

beforeEach(() => {
  resetMockAccount();
  mockRouter.replace.mockClear();
  passkeys.outcome = 'approve';
  passkeys.asserted = [];
  setPasskeys(passkeys);
  tokenStore = new MemoryTokenStore({ refreshToken: 'mock-refresh', serverUrl: 'https://pc.tailnet.ts.net' });
  configureAuth({ platform: 'web', tokenStore });
});
afterEach(() => resetAuth());

describe('DeleteAccountScreen', () => {
  it('explains what will happen before anything is deleted', async () => {
    await renderWithProviders(<DeleteAccountScreen />);

    expect(screen.getByText('Your library and photos are deleted within 30 days.')).toBeTruthy();
    expect(screen.getAllByText(/can't be undone/).length).toBeGreaterThan(0);
    expect(mockAccount.deleted).toBe(false);
  });

  it('sends nothing when the passkey prompt is cancelled', async () => {
    passkeys.outcome = 'cancel';
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText(CONFIRM));

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
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
    expect(await tokenStore.load()).not.toBeNull();
    expect(mockRouter.replace).not.toHaveBeenCalled();
    unregister();
  });

  it('clears local data and goes to sign-in after a fresh passkey', async () => {
    const cleared = jest.fn();
    const unregister = registerLocalDataClearer(cleared);
    await renderWithProviders(<DeleteAccountScreen />);

    await fireEvent.press(screen.getByLabelText(CONFIRM));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(passkeys.asserted).toHaveLength(1);
    expect(mockAccount.deleted).toBe(true);
    expect(cleared).toHaveBeenCalled();
    expect(await tokenStore.load()).toBeNull();
    unregister();
  });

  it('on the iPhone, explains where to delete the account instead of offering the button', async () => {
    configureAuth({ platform: 'native' });
    await renderWithProviders(<DeleteAccountScreen />);

    expect(screen.queryByLabelText(CONFIRM)).toBeNull();
    expect(screen.getByText(/open CollectionMaxxing in a browser on your computer/)).toBeTruthy();
  });
});
