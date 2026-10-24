import { fireEvent, screen } from '@testing-library/react-native';
import { renderWithProviders } from '@/test/render';
import { BackButton } from './BackButton';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

beforeEach(() => mockRouter.back.mockClear());

describe('BackButton', () => {
  it('goes back when pressed', async () => {
    await renderWithProviders(<BackButton />);

    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.back).toHaveBeenCalled();
  });
});
