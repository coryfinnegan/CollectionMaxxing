import { act, fireEvent, render, screen } from '@testing-library/react-native';
import { ReauthCancelledError } from '@/lib/auth/errors';
import { ReauthPrompt, requestReauth } from './reauth';
import { setNavigator } from './services';

const navigate = jest.fn();
beforeEach(() => {
  navigate.mockClear();
  setNavigator(navigate);
});

describe('ReauthPrompt', () => {
  it('is closed until the server asks for a fresh sign-in', async () => {
    await render(<ReauthPrompt />);

    expect(screen.queryByText("Confirm it's you")).toBeNull();
  });

  it('explains why, then sends the browser through a fresh sign-in and back to this page', async () => {
    await render(<ReauthPrompt />);
    await act(async () => {
      void requestReauth();
    });

    expect(screen.getByText("Confirm it's you")).toBeTruthy();
    expect(screen.getByText(/needs a fresh sign-in/)).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Sign in again'));

    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate.mock.calls[0][0]).toMatch(/^\/account\/login\?returnUrl=.*&reauth=1$/);
    expect(screen.queryByText("Confirm it's you")).toBeNull();
  });

  it('"Not now" fails the waiting request with a cancel and does not navigate', async () => {
    await render(<ReauthPrompt />);
    let outcome: unknown;
    await act(async () => {
      requestReauth().catch((e) => (outcome = e));
    });

    await fireEvent.press(screen.getByLabelText('Not now'));

    await act(async () => {});
    expect(outcome).toBeInstanceOf(ReauthCancelledError);
    expect(navigate).not.toHaveBeenCalled();
  });
});
