// Tab viewports end above the floating phone chrome; the in-flow web bar needs nothing.
import { Platform, StyleSheet } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { Text } from '@/ui';
import { SCAN_PILL_CLEARANCE, TAB_BAR_CLEARANCE, TabBottomPad, tabBottomClearance } from './TabBottomPad';

const originalOs = Platform.OS;
afterEach(() => {
  Platform.OS = originalOs;
});

describe('tabBottomClearance', () => {
  it('clears the bar, the pill and the home indicator on the phone', () => {
    Platform.OS = 'ios';
    expect(tabBottomClearance(true, 34)).toBe(34 + TAB_BAR_CLEARANCE + SCAN_PILL_CLEARANCE);
  });

  it('skips the pill on the Scan tab', () => {
    Platform.OS = 'ios';
    expect(tabBottomClearance(false, 34)).toBe(34 + TAB_BAR_CLEARANCE);
  });

  it('keeps no clearance on the web, where the bar sits in flow', () => {
    Platform.OS = 'web';
    expect(tabBottomClearance(true, 0)).toBe(0);
    expect(tabBottomClearance(false, 0)).toBe(0);
  });
});

describe('TabBottomPad', () => {
  it('pads its viewport above the bar and the pill', async () => {
    Platform.OS = 'ios';
    const { getByTestId } = await renderWithProviders(
      <TabBottomPad withPill>
        <Text>Library</Text>
      </TabBottomPad>,
    );

    // Jest safe-area insets are zero, so the pad is the chrome alone.
    expect(StyleSheet.flatten(getByTestId('tab-bottom-pad').props.style).paddingBottom).toBe(TAB_BAR_CLEARANCE + SCAN_PILL_CLEARANCE);
  });

  it('keeps no pad on the web', async () => {
    Platform.OS = 'web';
    const { getByTestId } = await renderWithProviders(
      <TabBottomPad withPill>
        <Text>Library</Text>
      </TabBottomPad>,
    );

    expect(StyleSheet.flatten(getByTestId('tab-bottom-pad').props.style).paddingBottom).toBe(0);
  });
});
