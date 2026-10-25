// Web layout at 768px and up: the sidebar from docs/06 "Navigation map".
import { type Href, Link, usePathname } from 'expo-router';
import { Pressable, View } from 'react-native';
import { LibrarySwitcher } from '@/features/onboarding/LibrarySwitcher';
import { radius, spacing, useColors } from '@/theme/tokens';
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
  const colors = useColors();
  const pathname = usePathname();
  return (
    <View
      role="navigation"
      style={{
        width: 220,
        padding: spacing.lg,
        gap: spacing.xs,
        backgroundColor: colors.surface,
        borderRightWidth: 1,
        borderRightColor: colors.line,
      }}
    >
      <Text variant="heading" style={{ marginBottom: spacing.lg }}>
        CollectionMaxxing
      </Text>
      <LibrarySwitcher />
      {sidebarItems.map((item) => {
        const active = pathname === item.href;
        return (
          <Link key={item.label} href={item.href} asChild>
            <Pressable
              accessibilityRole="link"
              {...(active ? { 'aria-current': 'page' as const } : {})}
              style={{
                paddingVertical: spacing.sm,
                paddingHorizontal: spacing.md,
                borderRadius: radius.md,
                backgroundColor: active ? colors.ground : 'transparent',
              }}
            >
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
