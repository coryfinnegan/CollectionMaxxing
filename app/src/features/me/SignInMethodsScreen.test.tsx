import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { setNavigator } from '@/features/auth/services';
import { ReauthCancelledError } from '@/lib/auth/errors';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { renderWithProviders } from '@/test/render';
import { SignInMethodsScreen } from './SignInMethodsScreen';

let mockParams: Record<string, string> = {};
const mockRouter = { push: jest.fn(), replace: jest.fn(), back: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter, useLocalSearchParams: () => mockParams }));

const navigate = jest.fn();
beforeEach(() => {
  resetMockAuth();
  mockParams = {};
  navigate.mockClear();
  setNavigator(navigate);
  configureAuth({ platform: 'web' });
});
afterEach(() => resetAuth());

describe('SignInMethodsScreen', () => {
  it('lists the accounts that can sign in, marking the one in use', async () => {
    await renderWithProviders(<SignInMethodsScreen />);

    expect(await screen.findByText('Google (this sign-in)')).toBeTruthy();
    expect(screen.getByText(/cory@example.com/)).toBeTruthy();
    expect(screen.getByText('Apple')).toBeTruthy();
  });

  it('Add goes to the sign-in in link mode and returns to this page', async () => {
    await renderWithProviders(<SignInMethodsScreen />);
    await screen.findByText('Apple');

    await fireEvent.press(screen.getByLabelText('Add a sign-in method'));

    expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Fme%2Fsign-in-methods&link=1');
  });

  it('asks before removing, then removes', async () => {
    await renderWithProviders(<SignInMethodsScreen />);
    await screen.findByText('Apple');

    await fireEvent.press(screen.getByLabelText('Remove Apple'));
    expect(screen.getByText('Remove Apple?')).toBeTruthy();
    expect(mockAuth.methods).toHaveLength(2); // nothing happens until confirmed
    await fireEvent.press(screen.getByLabelText('Remove'));

    await waitFor(() => expect(mockAuth.methods).toHaveLength(1));
    await waitFor(() => expect(screen.queryByText('Apple')).toBeNull());
  });

  it('cancelling keeps the method', async () => {
    await renderWithProviders(<SignInMethodsScreen />);
    await screen.findByText('Apple');

    await fireEvent.press(screen.getByLabelText('Remove Apple'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText('Remove Apple?')).toBeNull();
    expect(mockAuth.methods).toHaveLength(2);
  });

  it('hides Remove when only one method is left', async () => {
    mockAuth.methods = mockAuth.methods.slice(0, 1);
    await renderWithProviders(<SignInMethodsScreen />);

    expect(await screen.findByText('Google (this sign-in)')).toBeTruthy();
    expect(screen.queryByLabelText(/^Remove/)).toBeNull();
    expect(screen.getByText(/only way to sign in/)).toBeTruthy();
  });

  it('removing needs a fresh sign-in: declining it removes nothing and says so', async () => {
    mockAuth.requireReauth = true;
    configureAuth({ platform: 'web', reauth: () => Promise.reject(new ReauthCancelledError()) });
    await renderWithProviders(<SignInMethodsScreen />);
    await screen.findByText('Apple');

    await fireEvent.press(screen.getByLabelText('Remove Apple'));
    await fireEvent.press(screen.getByLabelText('Remove'));

    expect(await screen.findByText('Okay, nothing changed.')).toBeTruthy();
    expect(mockAuth.methods).toHaveLength(2);
  });

  it.each([
    ['already_linked', /already used by someone else/],
    ['reauth_failed', /couldn't confirm it's you/],
  ])('explains ?error=%s after coming back from the sign-in', async (error, copy) => {
    mockParams = { error };
    await renderWithProviders(<SignInMethodsScreen />);

    expect(screen.getByText(copy)).toBeTruthy();
    await screen.findByText('Apple');
  });
});
