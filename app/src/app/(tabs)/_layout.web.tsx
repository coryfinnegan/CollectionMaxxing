// The web tab layout (#303): headless tabs around the current page. The root
// web layout owns the sidebar, skip link, and main landmark; this file only
// switches tab pages and draws the narrow-screen bar. The triggers stay inline:
// headless tabs find them by element, so a wrapper component would hide them.
import { Tabs, TabList, TabSlot, TabTrigger } from 'expo-router/ui';
import { usePathname, type Href } from 'expo-router';
import { useWindowDimensions } from 'react-native';
import { TabsFrame } from '@/components/TabsFrame';
import { spacing, useColors, WIDE_BREAKPOINT } from '@/theme/tokens';
import { Text } from '@/ui';

const visibleTabs: { name: string; href: Href; label: string }[] = [
  { name: 'library', href: '/library', label: 'Library' },
  { name: 'me', href: '/me', label: 'Me' },
];

export default function TabsLayout() {
  const colors = useColors();
  const pathname = usePathname();
  const { width } = useWindowDimensions();
  const narrow = width < WIDE_BREAKPOINT;
  return (
    <TabsFrame>
      <Tabs>
        <TabSlot />
        {/* The list always renders with the same triggers: triggers define which
            routes exist, so hiding the bar only hides the container — the tabs
            stay declared on wide screens too. */}
        <TabList
          role="tablist"
          accessibilityLabel="Tabs"
          style={
            narrow
              ? { flexDirection: 'row', backgroundColor: colors.surface, borderTopWidth: 1, borderTopColor: colors.line }
              : { display: 'none' }
          }
        >
          {visibleTabs.map((tab) => {
            const active = pathname === tab.href;
            return (
              <TabTrigger
                key={tab.name}
                name={tab.name}
                href={tab.href}
                accessibilityRole="tab"
                accessibilityLabel={tab.label}
                aria-selected={active}
                style={{ flex: 1, minHeight: 44, alignItems: 'center', justifyContent: 'center', paddingVertical: spacing.sm }}
              >
                <Text variant="label" tone={active ? 'accent' : 'muted'}>
                  {tab.label}
                </Text>
              </TabTrigger>
            );
          })}
          <TabTrigger name="scan" href="/scan" style={{ display: 'none' }} />
        </TabList>
      </Tabs>
    </TabsFrame>
  );
}
