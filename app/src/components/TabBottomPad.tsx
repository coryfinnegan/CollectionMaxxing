// The bottom clearance tab content needs on the phone (#426): the iOS 26
// floating glass bar and, off the Scan tab, the Scan pill above it overlay
// content, so tab viewports end above both instead of scrolling under them.
// The web tab bar sits in flow, so the web keeps no clearance. Measured chrome
// heights plus the home-indicator inset; re-measure on device (TestFlight) if
// the glass metrics change.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { Platform, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { StyleSheet } from 'react-native-unistyles';

/** The floating glass bar plus breathing room. */
export const TAB_BAR_CLEARANCE = 57;
/** The Scan pill plus breathing room; the Scan tab itself shows no pill. */
export const SCAN_PILL_CLEARANCE = 52;

export function tabBottomClearance(withPill: boolean, bottomInset: number): number {
  if (Platform.OS === 'web') return 0;
  return bottomInset + TAB_BAR_CLEARANCE + (withPill ? SCAN_PILL_CLEARANCE : 0);
}

/** Pads a tab viewport above the floating chrome. The Scan tab passes no pill and a black backdrop. */
export function TabBottomPad({ withPill, background, children }: { withPill: boolean; background?: string; children: ReactNode }) {
  const insets = useSafeAreaInsets();
  return (
    <View testID="tab-bottom-pad" style={styles.pad(tabBottomClearance(withPill, insets.bottom), background)}>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  pad: (clearance: number, background?: string) => ({ flex: 1, paddingBottom: clearance, backgroundColor: background }),
});
