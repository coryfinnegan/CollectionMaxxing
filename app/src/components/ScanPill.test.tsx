// Scan primacy (#303): a glass pill above the native tab bar takes you to the
// Scan tab, and collapses to a barcode button when the bar minimizes. Stateless:
// two instances render at once (one per placement) with unshared state.
import { fireEvent, screen } from '@testing-library/react-native';
import { View } from 'react-native';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { renderWithProviders } from '@/test/render';
import { ScanPill } from './ScanPill';

const mockRouter = { navigate: jest.fn(), push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const mockUsePlacement = jest.fn((): 'regular' | 'inline' => 'regular');
jest.mock('expo-router/unstable-native-tabs', () => ({
  NativeTabs: { BottomAccessory: { usePlacement: () => mockUsePlacement() } },
}));

const fakeHaptics = () => {
  const calls: string[] = [];
  const driver: HapticDriver = {
    tap: () => void calls.push('tap'),
    mediumTap: () => void calls.push('medium'),
    longTap: () => void calls.push('long'),
    tick: () => void calls.push('tick'),
    success: () => void calls.push('success'),
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  return calls;
};

beforeEach(() => {
  mockRouter.navigate.mockClear();
  mockUsePlacement.mockReturnValue('regular');
});

afterEach(() => setHapticDriver(null));

describe('ScanPill', () => {
  it('offers a Scan a barcode pill', async () => {
    await renderWithProviders(<ScanPill />);

    expect(screen.getByText('Scan a barcode')).toBeTruthy();
  });

  it('opens the Scan tab with a selection tick when pressed', async () => {
    const calls = fakeHaptics();
    await renderWithProviders(<ScanPill />);

    await fireEvent.press(screen.getByRole('button', { name: 'Scan a barcode' }));

    expect(mockRouter.navigate).toHaveBeenCalledWith('/scan');
    expect(calls).toEqual(['tick']);
  });

  it('collapses to a barcode button when the bar minimizes', async () => {
    mockUsePlacement.mockReturnValue('inline');
    await renderWithProviders(<ScanPill />);

    // The full label does not fit beside a minimized bar, but the button keeps its name.
    expect(screen.queryByText('Scan a barcode')).toBeNull();
    await fireEvent.press(screen.getByRole('button', { name: 'Scan a barcode' }));
    expect(mockRouter.navigate).toHaveBeenCalledWith('/scan');
  });

  it('is stateless: two instances press alike', async () => {
    await renderWithProviders(
      <View>
        <ScanPill />
        <ScanPill />
      </View>,
    );

    const pills = screen.getAllByRole('button', { name: 'Scan a barcode' });
    expect(pills).toHaveLength(2);
    await fireEvent.press(pills[1]);
    expect(mockRouter.navigate).toHaveBeenCalledWith('/scan');
  });
});
