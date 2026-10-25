// Web layout at 768px and up: the sidebar from docs/06 "Navigation map".
import { type Href, Link, usePathname } from 'expo-router';
import { Pressable, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { LibrarySwitcher } from '@/features/onboarding/LibrarySwitcher';
import { Text } from '@/ui';

export const sidebarItems: { label: string; href: Href }[] = [
  { label: 'Library', href: '/library' },
  { label: 'Wishlist', href: '/wishlist' },
  { label: 'Lent out', href: '/lent-out' },
  { label: 'Locations', href: '/me/locations' },
  { label: 'Members', href: '/members' },
  { label: 'Import and export', href: '/me/export' },
  { label: 'Devices', href: '/me/devices' },
  { label: 'Settings', href: '/settings' },
];

export function Sidebar() {
  const pathname = usePathname();
  return (
    <View role="navigation" style={styles.sidebar}>
      <Text variant="heading" style={styles.brand}>
        CollectionMaxxing
      </Text>
      <LibrarySwitcher />
      {sidebarItems.map((item) => {
        const active = pathname === item.href;
        return (
          <Link key={item.label} href={item.href} asChild>
            <Pressable accessibilityRole="link" {...(active ? { 'aria-current': 'page' as const } : {})} style={styles.item(active)}>
              <Text variant="label" tone={active ? 'accent' : 'ink'}>
                {item.label}
              </Text>
            </Pressable>
          </Link>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  sidebar: {
    width: 220,
    padding: theme.spacing.lg,
    gap: theme.spacing.xs,
    backgroundColor: theme.colors.surface,
    borderRightWidth: 1,
    borderRightColor: theme.colors.line,
  },
  brand: { marginBottom: theme.spacing.lg },
  item: (active: boolean) => ({
    paddingVertical: theme.spacing.sm,
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.md,
    backgroundColor: active ? theme.colors.ground : 'transparent',
  }),
}));
