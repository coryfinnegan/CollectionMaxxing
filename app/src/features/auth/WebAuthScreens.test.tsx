import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { resetAuth } from '@/lib/auth/session';
import { MOCK_INVITE, MOCK_SETUP_CODE } from '@/mocks/auth';
import { renderWithProviders } from '@/test/render';
import { FakePasskeyClient } from './passkeys';
import { setPasskeys } from './services';
import { JoinScreen, LoginScreen, SetupScreen } from './WebAuthScreens';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

let passkeys: FakePasskeyClient;
beforeEach(() => {
  mockRouter.replace.mockClear();
  passkeys = new FakePasskeyClient();
  setPasskeys(passkeys);
});
afterEach(() => resetAuth());

async function fillSetup(code: string, name = 'Cory') {
  await fireEvent.changeText(screen.getByLabelText('Setup code'), code);
  await fireEvent.changeText(screen.getByLabelText('Your name'), name);
  await fireEvent.press(screen.getByLabelText('Create passkey'));
}

describe('SetupScreen', () => {
  it('creates the owner with a passkey and opens the library', async () => {
    await renderWithProviders(<SetupScreen />);
    await fillSetup(MOCK_SETUP_CODE);

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(passkeys.created).toHaveLength(1);
  });

  it('says so when the passkey prompt is cancelled, and does not move on', async () => {
    passkeys.outcome = 'cancel';
    await renderWithProviders(<SetupScreen />);
    await fillSetup(MOCK_SETUP_CODE);

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('explains an expired setup code', async () => {
    await renderWithProviders(<SetupScreen />);
    await fillSetup('CM-EXPIRED');

    expect(await screen.findByText(/setup code has expired/)).toBeTruthy();
    expect(passkeys.created).toHaveLength(0);
  });

  it('explains a wrong setup code', async () => {
    await renderWithProviders(<SetupScreen />);
    await fillSetup('nope');

    expect(await screen.findByText(/setup code isn't right/)).toBeTruthy();
  });

  it('warns up front in a browser without passkeys', async () => {
    passkeys.supported = false;
    await renderWithProviders(<SetupScreen />);

    expect(screen.getByText(/can't use passkeys/)).toBeTruthy();
    expect(screen.getByLabelText('Create passkey').props.accessibilityState.disabled).toBe(true);
  });
});

describe('LoginScreen', () => {
  it('signs in with a passkey and opens the library', async () => {
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('Sign in with a passkey'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(passkeys.asserted).toHaveLength(1);
  });

  it('shows a friendly message when the prompt is cancelled', async () => {
    passkeys.outcome = 'cancel';
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('Sign in with a passkey'));

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
  });

  it('does not offer sign-in in an unsupported browser', async () => {
    passkeys.supported = false;
    await renderWithProviders(<LoginScreen />);

    expect(screen.getByText(/can't use passkeys/)).toBeTruthy();
    expect(screen.getByLabelText('Sign in with a passkey').props.accessibilityState.disabled).toBe(true);
  });

  it('links to setup for a first run', async () => {
    await renderWithProviders(<LoginScreen />);
    await fireEvent.press(screen.getByLabelText('First time here? Set up CollectionMaxxing'));

    expect(mockRouter.push).toHaveBeenCalledWith('/setup');
  });
});

describe('JoinScreen', () => {
  it('shows who invited you and creates a passkey', async () => {
    await renderWithProviders(<JoinScreen token={MOCK_INVITE} />);

    expect(await screen.findByText("Join Cory's library as Editor")).toBeTruthy();
    await fireEvent.changeText(screen.getByLabelText('Your name'), 'Sam');
    await fireEvent.press(screen.getByLabelText('Create passkey'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(passkeys.created).toHaveLength(1);
  });

  it.each(['expired-invite', 'used-invite', 'who-knows'])('shows the same message for the bad, used or expired token %s', async (token) => {
    await renderWithProviders(<JoinScreen token={token} />);

    expect(await screen.findByText("This invite link isn't valid anymore. Ask for a new one.")).toBeTruthy();
  });

  it('says so when the passkey prompt is cancelled', async () => {
    passkeys.outcome = 'cancel';
    await renderWithProviders(<JoinScreen token={MOCK_INVITE} />);
    await screen.findByText("Join Cory's library as Editor");
    await fireEvent.changeText(screen.getByLabelText('Your name'), 'Sam');
    await fireEvent.press(screen.getByLabelText('Create passkey'));

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });
});
