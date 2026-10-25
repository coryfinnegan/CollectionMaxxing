// The phone tab bar (#303): native tabs with SF Symbol icons in default/selected
// variants and equal labels. No background, blur, or label styling: iOS 26
// Liquid Glass owns the bar; tintColor names the selected tab. The triggers and
// the bottom accessory stay inline: native tabs find them by element, so a
// wrapper component would hide them.
import { NativeTabs } from 'expo-router/unstable-native-tabs';
import { usePathname } from 'expo-router';
import { useColors } from '@/theme/tokens';
import { ScanPill } from './ScanPill';

export function NativeTabBar() {
  const colors = useColors();
  const pathname = usePathname();
  return (
    <NativeTabs tintColor={colors.accent}>
      {/* The gate lives outside the accessory: two pill instances render at once
          (one per placement) with unshared state. */}
      <NativeTabs.BottomAccessory>{pathname === '/scan' ? null : <ScanPill />}</NativeTabs.BottomAccessory>
      <NativeTabs.Trigger name="library">
        <NativeTabs.Trigger.Label>Library</NativeTabs.Trigger.Label>
        <NativeTabs.Trigger.Icon sf={{ default: 'books.vertical', selected: 'books.vertical.fill' }} md="library_books" />
      </NativeTabs.Trigger>
      <NativeTabs.Trigger name="scan">
        <NativeTabs.Trigger.Label>Scan</NativeTabs.Trigger.Label>
        <NativeTabs.Trigger.Icon sf="barcode.viewfinder" md="barcode_scanner" />
      </NativeTabs.Trigger>
      <NativeTabs.Trigger name="me/index">
        <NativeTabs.Trigger.Label>Me</NativeTabs.Trigger.Label>
        <NativeTabs.Trigger.Icon sf={{ default: 'person.crop.circle', selected: 'person.crop.circle.fill' }} md="person" />
      </NativeTabs.Trigger>
    </NativeTabs>
  );
}
