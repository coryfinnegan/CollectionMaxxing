// Web layout at 768px and up: the sidebar from docs/06 "Navigation map". The wide layout has no Me tab, so the pages Me links to are
// listed here directly: Sold for everyone, and Recently deleted for owners and editors (the same rule Me uses).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { type Href, Link, usePathname } from 'expo-router';
import { Pressable, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useMyRole } from '@/features/me/useMyRole';
import { LibrarySwitcher } from '@/features/onboarding/LibrarySwitcher';
import { Text } from '@/ui';

type SidebarItem = { label: string; href: Href };

/** The sidebar's links, top to bottom. `canEdit` (an owner or an editor, known once the role has arrived) adds Recently deleted. */
export function sidebarItemsFor(canEdit: boolean): SidebarItem[] {
  return [
    { label: 'Library', href: '/library' },
    { label: 'Wishlist', href: '/wishlist' },
    { label: 'Lent out', href: '/lent-out' },
    { label: 'Locations', href: '/me/locations' },
    { label: 'Members', href: '/members' },
    { label: 'Import and export', href: '/me/export' },
    { label: 'Sold', href: '/sold' },
    ...(canEdit ? [{ label: 'Recently deleted', href: '/me/deleted' } as SidebarItem] : []),
    { label: 'Devices', href: '/me/devices' },
    { label: 'Settings', href: '/settings' },
  ];
}

export function Sidebar() {
  const pathname = usePathname();
  const { canEdit } = useMyRole();
  return (
    <View role="navigation" style={styles.sidebar}>
      <Text variant="heading" style={styles.brand}>
        CollectionMaxxing
      </Text>
      <LibrarySwitcher />
      {sidebarItemsFor(canEdit).map((item) => {
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
