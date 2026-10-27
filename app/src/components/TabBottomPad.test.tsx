// Tab viewports end above the floating phone bar; the in-flow web bar needs nothing. No view reserves room for
// the removed Scan pill (#432).
import { Platform, StyleSheet } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { Text } from '@/ui';
import { TAB_BAR_CLEARANCE, TabBottomPad, tabBottomClearance } from './TabBottomPad';

const originalOs = Platform.OS;
afterEach(() => {
  Platform.OS = originalOs;
});

describe('tabBottomClearance', () => {
  it('clears the bar and the home indicator on the phone, with no pill term', () => {
    Platform.OS = 'ios';
    expect(tabBottomClearance(34)).toBe(34 + TAB_BAR_CLEARANCE);
  });

  it('keeps no clearance on the web, where the bar sits in flow', () => {
    Platform.OS = 'web';
    expect(tabBottomClearance(0)).toBe(0);
  });
});

describe('TabBottomPad', () => {
  it('pads its viewport above the bar alone', async () => {
    Platform.OS = 'ios';
    const { getByTestId } = await renderWithProviders(
      <TabBottomPad>
        <Text>Library</Text>
      </TabBottomPad>,
    );

    // Jest safe-area insets are zero, so the pad is the bar clearance alone.
    expect(StyleSheet.flatten(getByTestId('tab-bottom-pad').props.style).paddingBottom).toBe(TAB_BAR_CLEARANCE);
  });

  it('keeps no pad on the web', async () => {
    Platform.OS = 'web';
    const { getByTestId } = await renderWithProviders(
      <TabBottomPad>
        <Text>Library</Text>
      </TabBottomPad>,
    );

    expect(StyleSheet.flatten(getByTestId('tab-bottom-pad').props.style).paddingBottom).toBe(0);
  });
});
