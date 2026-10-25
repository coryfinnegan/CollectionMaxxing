// The detail header (#306): a dark band derived from the cover when there is
// one, the token surface when there is not. Either way the text on it passes AA.
import { screen, waitFor } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { itemsState, resetItems } from '@/mocks/items';
import { renderWithProviders } from '@/test/render';
import { fixed, lightColors } from '@/theme/tokens';
import { headerThemeFromCover } from './coverColor';
import { ItemDetailScreen } from './ItemDetailScreen';
import { clearCoverAccents } from './useCoverAccent';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

jest.mock('react-native-image-colors', () => ({ getColors: (...args: unknown[]) => mockGetColors(...args) }));
const mockGetColors = jest.fn();

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const entry = () => itemsState.entries.find((e) => e.item.id === MATRIX)!;

const flat = (testID: string) => StyleSheet.flatten(screen.getByTestId(testID).props.style);

beforeEach(() => {
  resetItems();
  clearCoverAccents();
  mockGetColors.mockReset();
  mockGetColors.mockResolvedValue({
    dominant: '#111111',
    vibrant: '#222222',
    darkVibrant: '#333333',
    lightVibrant: '#444444',
    darkMuted: '#555555',
    lightMuted: '#666666',
    muted: '#e11d48',
    platform: 'web',
  });
});

describe('ItemDetailScreen header', () => {
  it('tones the cover into a dark band with readable text', async () => {
    entry().release.coverUrl = 'https://covers.test/matrix.jpg';
    entry().work.creators = ['The Wachowskis'];
    const expected = headerThemeFromCover('#e11d48', fixed.white, lightColors.ink)!;

    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await waitFor(() => expect(flat('Detail header').backgroundColor).toBe(expected.background));
    expect(StyleSheet.flatten(screen.getByText('The Wachowskis').props.style).color).toBe(expected.text);
  });

  it('falls back to the token surface, with muted text, when there is no cover', async () => {
    entry().work.creators = ['The Wachowskis'];

    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(mockGetColors).not.toHaveBeenCalled();
    expect(flat('Detail header').backgroundColor).toBe(lightColors.surface);
    expect(StyleSheet.flatten(screen.getByText('The Wachowskis').props.style).color).toBe(lightColors.muted);
  });

  it('falls back to the token surface when the palette cannot be read', async () => {
    entry().release.coverUrl = 'https://covers.test/matrix.jpg';
    mockGetColors.mockRejectedValueOnce(new Error('offline'));

    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await waitFor(() => expect(mockGetColors).toHaveBeenCalled());
    await waitFor(() => expect(flat('Detail header').backgroundColor).toBe(lightColors.surface));
  });
});
