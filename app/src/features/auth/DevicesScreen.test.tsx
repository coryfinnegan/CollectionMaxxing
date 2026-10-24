import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { DevicesScreen } from './DevicesScreen';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const CURRENT = 'This browser (Edge on Windows)';
const failing = () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 });

beforeEach(() => {
  resetMockAuth();
  mockRouter.replace.mockClear();
  configureAuth({ platform: 'web' });
});
afterEach(() => resetAuth());

describe('DevicesScreen', () => {
  it('lists browsers and iPhones with when they were last seen', async () => {
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText('Cory’s iPhone')).toBeTruthy();
    expect(screen.getByText(`${CURRENT} (this browser)`)).toBeTruthy();
    expect(screen.getByText('Safari on Mac')).toBeTruthy();
    expect(screen.getAllByText(/Last seen/).length).toBe(3);
  });

  it('no longer offers to add a device with a QR code', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    expect(screen.queryByLabelText('Add a device')).toBeNull();
    expect(screen.queryByTestId('qr-code')).toBeNull();
  });

  it('asks before signing a browser out, then does it', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Safari on Mac');

    await fireEvent.press(screen.getByLabelText('Sign out Safari on Mac'));
    expect(screen.getByText('Sign out Safari on Mac?')).toBeTruthy();
    expect(mockAuth.sessions).toHaveLength(2); // nothing happens until confirmed

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(screen.queryByText('Safari on Mac')).toBeNull());
    expect(mockAuth.sessions).toHaveLength(1);
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('cancelling the confirmation keeps the browser', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Safari on Mac');

    await fireEvent.press(screen.getByLabelText('Sign out Safari on Mac'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText('Sign out Safari on Mac?')).toBeNull();
    expect(mockAuth.sessions).toHaveLength(2);
  });

  it('warns before this browser signs itself out, then goes to the sign-in page', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Safari on Mac');

    await fireEvent.press(screen.getByLabelText(`Sign out ${CURRENT}`));
    expect(screen.getByText(/the browser you're using right now/)).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Sign out this browser'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
  });

  it('signs out of every browser after asking, keeps the iPhones, and goes to the sign-in page', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out of all browsers'));
    expect(screen.getByText('Sign out of all browsers?')).toBeTruthy();
    expect(mockAuth.sessions).toHaveLength(2);

    await fireEvent.press(screen.getByLabelText('Sign out all browsers'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(mockAuth.sessions).toHaveLength(0);
    expect(mockAuth.iphones).toHaveLength(1);
  });

  it('signs out all iPhones after asking, and stays on the page with the list refreshed', async () => {
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out all iPhones'));
    expect(screen.getByText('Sign out all iPhones?')).toBeTruthy();
    expect(mockAuth.iphones).toHaveLength(1); // nothing happens until confirmed

    await fireEvent.press(screen.getAllByLabelText('Sign out all iPhones')[0]);

    await waitFor(() => expect(screen.queryByText('Cory’s iPhone')).toBeNull());
    expect(mockAuth.iphones).toHaveLength(0);
    expect(mockRouter.replace).not.toHaveBeenCalled();
    expect(screen.getByText(CURRENT + ' (this browser)')).toBeTruthy();
  });

  it('does not offer "Sign out all iPhones" when there are none', async () => {
    mockAuth.iphones = [];
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText(/No iPhones yet/)).toBeTruthy();
    expect(screen.queryByLabelText('Sign out all iPhones')).toBeNull();
  });

  it('on the phone, signing out of all browsers stays on Devices and keeps the iPhones', async () => {
    configureAuth({ platform: 'native' });
    await renderWithProviders(<DevicesScreen />);
    await screen.findByText('Cory’s iPhone');

    await fireEvent.press(screen.getByLabelText('Sign out of all browsers'));
    await fireEvent.press(screen.getByLabelText('Sign out all browsers'));

    await waitFor(() => expect(screen.queryByText(/This browser/)).toBeNull());
    expect(screen.getByText('Cory’s iPhone')).toBeTruthy();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('shows the iPhones that loaded and a friendly note when the browsers list fails', async () => {
    server.use(http.get('*/api/v1/me/web-sessions', failing));
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText('Cory’s iPhone')).toBeTruthy();
    expect(await screen.findByText("Couldn't load your browsers. Try again in a moment.")).toBeTruthy();
  });

  it('shows the browsers that loaded and a friendly note when the iPhones list fails', async () => {
    server.use(http.get('*/api/v1/me/app-sign-ins', failing));
    await renderWithProviders(<DevicesScreen />);

    expect(await screen.findByText(`${CURRENT} (this browser)`)).toBeTruthy();
    expect(await screen.findByText("Couldn't load your iPhones. Try again in a moment.")).toBeTruthy();
  });

  it('shows the friendly error when both lists fail', async () => {
    server.use(http.get('*/api/v1/me/app-sign-ins', failing), http.get('*/api/v1/me/web-sessions', failing));
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

  it('offers a way back to the previous screen', async () => {
    mockRouter.back.mockClear();
    await renderWithProviders(<DevicesScreen />);

    await fireEvent.press(await screen.findByRole('button', { name: 'Back' }));
    expect(mockRouter.back).toHaveBeenCalledTimes(1);
  });
});
