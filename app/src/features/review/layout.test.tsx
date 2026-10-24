// Layout quality (#269): Review respects the safe area.
import { screen } from '@testing-library/react-native';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { setTrayStore } from '@/features/scan/tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { ReviewRoute } from './ReviewRoute';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
});
afterEach(() => setTrayStore(undefined));

describe('review layout', () => {
  it('keeps Review inside the safe area', async () => {
    await renderWithProviders(<ReviewRoute />);
    expect(await screen.findByText('Nothing to review. Scan something and it shows up here.')).toBeTruthy();
    expect(screen.getByTestId('review-safe-area')).toBeTruthy();
  });
});
