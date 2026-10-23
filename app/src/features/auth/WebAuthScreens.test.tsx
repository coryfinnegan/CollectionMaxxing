import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { resetAuth } from '@/lib/auth/session';
import { MOCK_INVITE, MOCK_LOGOUT_URL, MOCK_SETUP_CODE } from '@/mocks/auth';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { setNavigator } from './services';
import { JoinScreen, LoginScreen, NotLinkedScreen, SetupScreen } from './WebAuthScreens';

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

async function submitSetup(code: string) {
  await fireEvent.changeText(screen.getByLabelText('Setup code'), code);
  await fireEvent.press(screen.getByLabelText('Continue'));
}

describe('SetupScreen', () => {
  it('sends the setup code and goes on to the sign-in the server names', async () => {
    await renderWithProviders(<SetupScreen />);
    await submitSetup(MOCK_SETUP_CODE);

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Flibrary'));
  });

  it('needs a code before it offers to continue', async () => {
    await renderWithProviders(<SetupScreen />);

    expect(screen.getByLabelText('Continue').props.accessibilityState.disabled).toBe(true);
  });

  it('explains an expired setup code', async () => {
    await renderWithProviders(<SetupScreen />);
    await submitSetup('CM-EXPIRED');

    expect(await screen.findByText(/setup code has expired/)).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('explains a wrong setup code', async () => {
    await renderWithProviders(<SetupScreen />);
    await submitSetup('nope');

    expect(await screen.findByText(/setup code isn't right/)).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('refuses a next step that leaves our own sign-in', async () => {
    server.use(http.post('*/setup', () => HttpResponse.json({ next: 'https://elsewhere.example/login' })));
    await renderWithProviders(<SetupScreen />);
    await submitSetup(MOCK_SETUP_CODE);

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });
});

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
    ['setup_closed', /Setup is already finished/],
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

  it('links to setup for a first run', async () => {
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('First time here? Set up CollectionMaxxing'));

    expect(mockRouter.push).toHaveBeenCalledWith('/setup');
  });
});

describe('JoinScreen', () => {
  it('shows who invited you, then signs in to join with the invite', async () => {
    await renderWithProviders(<JoinScreen token={MOCK_INVITE} />);

    expect(await screen.findByText("Join Cory's library as Editor")).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Sign in to join'));

    expect(navigate).toHaveBeenCalledWith(`/account/login?invite=${MOCK_INVITE}`);
  });

  it('does not offer to join until the invite is known', async () => {
    // Hold the preview until the button has been checked: an instant mock answer could otherwise win the race on a busy runner.
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('*/api/v1/invites/:token', async () => {
        await held;
        return HttpResponse.json({ ownerName: 'Cory', role: 'editor' });
      }),
    );
    await renderWithProviders(<JoinScreen token={MOCK_INVITE} />);

    expect(screen.getByLabelText('Sign in to join').props.accessibilityState.disabled).toBe(true);
    release();
    await screen.findByText("Join Cory's library as Editor");
    await waitFor(() => expect(screen.getByLabelText('Sign in to join').props.accessibilityState.disabled).toBe(false));
  });

  it.each(['expired-invite', 'used-invite', 'who-knows'])('shows the same message for the bad, used or expired token %s', async (token) => {
    await renderWithProviders(<JoinScreen token={token} />);

    expect(await screen.findByText("This invite link isn't valid anymore. Ask for a new one.")).toBeTruthy();
    expect(screen.queryByLabelText('Sign in to join')).toBeNull();
  });
});

describe('NotLinkedScreen', () => {
  it('says the account is not part of a library and points to the owner', async () => {
    await renderWithProviders(<NotLinkedScreen />);

    expect(screen.getByText("This account isn't part of a library. Ask the owner for an invite.")).toBeTruthy();
  });

  it.each(['Sign out', 'Try another account'])('%s ends the sign-in at Auth0 so another account can be chosen', async (label) => {
    await renderWithProviders(<NotLinkedScreen />);
    await fireEvent.press(screen.getByLabelText(label));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith(MOCK_LOGOUT_URL));
  });

  it('says so when the sign-out fails', async () => {
    server.use(http.post('*/account/logout', () => HttpResponse.json({ title: 'Oops', status: 500 }, { status: 500 })));
    await renderWithProviders(<NotLinkedScreen />);
    await fireEvent.press(screen.getByLabelText('Sign out'));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });
});
