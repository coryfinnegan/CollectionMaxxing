// The web tab layout (#303): headless tabs around the current page. The root
// web layout owns the sidebar, skip link, and main landmark; this file only
// switches tab pages and draws the narrow-screen bar. The triggers stay inline:
// headless tabs find them by element, so a wrapper component would hide them.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Tabs, TabList, TabSlot, TabTrigger } from 'expo-router/ui';
import { usePathname, type Href } from 'expo-router';
import { useWindowDimensions, type ViewStyle } from 'react-native';
import { useUnistyles } from 'react-native-unistyles';
import { TabsFrame } from '@/components/TabsFrame';
import { WIDE_BREAKPOINT } from '@/theme/tokens';
import { Text } from '@/ui';

const visibleTabs: { name: string; href: Href; label: string }[] = [
  { name: 'library', href: '/library', label: 'Library' },
  { name: 'me', href: '/me', label: 'Me' },
];

// Unistyles web styles only resolve inside babel-processed modules: sheet
// values passed to expo-router/ui (TabSlot, TabList, TabTrigger) arrive
// unstyled on web. This file stays on plain objects with theme values instead
// (#305); mobile.spec fails loudly if they ever stop resolving.
const slot: ViewStyle = { flex: 1 };
const hidden: ViewStyle = { display: 'none' };

export default function TabsLayout() {
  const { theme } = useUnistyles();
  const pathname = usePathname();
  const { width } = useWindowDimensions();
  const narrow = width < WIDE_BREAKPOINT;
  const tabBar: ViewStyle = narrow
    ? {
        flexDirection: 'row',
        borderTopWidth: 1,
        backgroundColor: theme.colors.surface,
        borderTopColor: theme.colors.line,
      }
    : hidden;
  const trigger: ViewStyle = {
    flex: 1,
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: theme.spacing.sm,
  };
  return (
    <TabsFrame>
      <Tabs>
        {/* flex:1 bounds the page to the viewport so long pages scroll above the pinned bar. */}
        <TabSlot style={slot} />
        {/* The list always renders with the same triggers: triggers define which
            routes exist, so hiding the bar only hides the container — the tabs
            stay declared on wide screens too. */}
        <TabList role="tablist" accessibilityLabel="Tabs" style={tabBar}>
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
                style={trigger}
              >
                <Text variant="label" tone={active ? 'accent' : 'muted'}>
                  {tab.label}
                </Text>
              </TabTrigger>
            );
          })}
          <TabTrigger name="scan" href="/scan" style={hidden} />
        </TabList>
      </Tabs>
    </TabsFrame>
  );
}
