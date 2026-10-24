import { fireEvent, screen } from '@testing-library/react-native';
import { resetAuth } from '@/lib/auth/session';
import { renderWithProviders } from '@/test/render';
import { setNavigator } from './services';
import { LoginScreen } from './WebAuthScreens';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
let mockParams: Record<string, string> = {};
jest.mock('expo-router', () => ({ useRouter: () => mockRouter, useLocalSearchParams: () => mockParams }));

const navigate = jest.fn();
beforeEach(() => {
  mockParams = {};
  navigate.mockClear();
  mockRouter.push.mockClear();
  setNavigator(navigate);
});
afterEach(() => resetAuth());

describe('LoginScreen', () => {
  it('navigates to the sign-in with a way back to the library', async () => {
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('Sign in'));

    expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Flibrary');
  });

  it('goes back to where the person was headed when that is a local path', async () => {
    mockParams = { returnUrl: '/members' };
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('Sign in'));

    expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Fmembers');
  });

  it.each(['https://elsewhere.example/', '//elsewhere.example/', '/\\elsewhere.example'])('ignores the return address %s', async (returnUrl) => {
    mockParams = { returnUrl };
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('Sign in'));

    expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Flibrary');
  });

  it.each([
    ['signin_failed', /didn't finish/],
    ['invite_invalid', /invite link isn't valid/],
  ])('explains ?error=%s', async (error, copy) => {
    mockParams = { error };
    await renderWithProviders(<LoginScreen />);

    expect(screen.getByText(copy)).toBeTruthy();
  });

  it('shows nothing for an error code it does not know', async () => {
    mockParams = { error: 'whatever' };
    await renderWithProviders(<LoginScreen />);

    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('keeps content clear of the notch and home indicator', async () => {
    await renderWithProviders(<LoginScreen />);

    expect(screen.getByTestId('safe-area')).toBeTruthy();
  });

  it('has no first-run setup button: every first sign-in makes an account', async () => {
    await renderWithProviders(<LoginScreen />);

    expect(screen.queryByLabelText(/First time here/)).toBeNull();
  });
});
