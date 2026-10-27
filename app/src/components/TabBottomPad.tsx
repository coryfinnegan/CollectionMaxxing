// The bottom clearance tab content needs on the phone (#426): the iOS 26
// floating glass bar overlays content, so tab viewports end above it instead of
// scrolling under it. The web tab bar sits in flow, so the web keeps no
// clearance. Measured bar height plus the home-indicator inset; re-measure on
// device (TestFlight) if the glass metrics change. No view reserves room for
// the removed Scan pill (#432).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { Platform, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { StyleSheet } from 'react-native-unistyles';

/** The floating glass bar plus breathing room. */
export const TAB_BAR_CLEARANCE = 57;

export function tabBottomClearance(bottomInset: number): number {
  if (Platform.OS === 'web') return 0;
  return bottomInset + TAB_BAR_CLEARANCE;
}

/** Pads a tab viewport above the floating bar. */
export function TabBottomPad({ children }: { children: ReactNode }) {
  const insets = useSafeAreaInsets();
  return (
    <View testID="tab-bottom-pad" style={styles.pad(tabBottomClearance(insets.bottom))}>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  pad: (clearance: number) => ({ flex: 1, paddingBottom: clearance }),
});
