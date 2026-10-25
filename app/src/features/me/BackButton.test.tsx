import { fireEvent, screen } from '@testing-library/react-native';
import { Platform } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { BackButton } from './BackButton';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const original = Platform.OS;
beforeEach(() => mockRouter.back.mockClear());
afterEach(() => {
  Platform.OS = original;
});

describe('BackButton', () => {
  it('goes back when pressed on the web, where pages have no header', async () => {
    Platform.OS = 'web';
    await renderWithProviders(<BackButton />);

    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.back).toHaveBeenCalled();
  });

  it('renders nothing on the phone, where the stack header already goes back', async () => {
    Platform.OS = 'ios';
    await renderWithProviders(<BackButton />);

    expect(screen.queryByLabelText('Back')).toBeNull();
  });
});
