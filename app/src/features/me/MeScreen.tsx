// The Me tab: item and member counts, then the jobs that live here.
import { type Href, useRouter } from 'expo-router';
import { useListMembers } from '@/api/generated/libraries/libraries';
import { useGetStats } from '@/api/generated/items/items';
import { isWeb } from '@/lib/auth/session';
import { Button, Card, ListRow, Screen, Text } from '@/ui';
import { useMyRole } from './useMyRole';

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

export function MeScreen() {
  const router = useRouter();
  const { libraryId, role, isError, refetch } = useMyRole();
  const stats = useGetStats(libraryId ?? '', { query: { enabled: !!libraryId } });
  const members = useListMembers(libraryId ?? '', { query: { enabled: !!libraryId } });
  const total = stats.data?.status === 200 ? stats.data.data.total : null;
  const memberCount = members.data?.status === 200 ? members.data.data.items.length : null;
  const counts = [total === null ? null : plural(total, 'item', 'items'), memberCount === null ? null : plural(memberCount, 'member', 'members')]
    .filter(Boolean)
    .join(' · ');

  const links: { title: string; subtitle?: string; href: Href }[] = [
    { title: 'Locations', href: '/me/locations' },
    { title: 'Export my data', href: '/me/export' },
    { title: 'Devices and browsers', href: '/me/devices' },
    ...(isWeb() ? [{ title: 'Sign-in methods', href: '/me/sign-in-methods' as Href }] : []),
    ...(role === 'owner' || role === 'editor' ? [{ title: 'Recently deleted', href: '/me/deleted' as Href }] : []),
    { title: 'Settings', subtitle: isWeb() ? undefined : 'Recognition, appearance and security', href: '/settings' },
    // Owners invite people and set roles; everyone else sees who is here. Nothing waits for a role, so no subtitle until it arrives.
    {
      title: 'Members',
      subtitle: role === 'owner' ? 'Invite people and manage roles' : role ? 'See who shares this library' : undefined,
      href: '/members',
    },
  ];

  return (
    <Screen title="Me" showTitleOnPhone>
      <Card>
        <Text variant="heading">{isWeb() ? 'This browser' : 'Paired: this iPhone'}</Text>
        {counts ? (
          <Text tone="muted">{counts}</Text>
        ) : isError ? (
          <>
            <Text tone="danger" accessibilityRole="alert">{"Couldn't load your library"}</Text>
            <Button label="Try again" variant="secondary" onPress={() => void refetch()} />
          </>
        ) : (
          <Text tone="muted">Loading…</Text>
        )}
      </Card>
      <Card>
        {links.map((link) => (
          <ListRow key={link.title} title={link.title} subtitle={link.subtitle} onPress={() => router.push(link.href)} trailing={<Text tone="muted">›</Text>} />
        ))}
      </Card>
      <Card>
        <ListRow title="Delete account" onPress={() => router.push('/me/delete')} trailing={<Text tone="muted">›</Text>} />
      </Card>
    </Screen>
  );
}
