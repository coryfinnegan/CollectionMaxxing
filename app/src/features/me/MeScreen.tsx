// The Me tab: item and member counts, then the jobs that live here.
import { type Href, useRouter } from 'expo-router';
import { useListMembers } from '@/api/generated/libraries/libraries';
import { useGetStats } from '@/api/generated/items/items';
import { isWeb } from '@/lib/auth/session';
import { Card, ListRow, Screen, Text } from '@/ui';
import { CollectionStats } from './CollectionStats';
import { useMyRole } from './useMyRole';

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

export function MeScreen() {
  const router = useRouter();
  const { libraryId, role } = useMyRole();
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
    { title: 'Members', subtitle: 'Managed on the web', href: '/members' },
  ];

  return (
    <Screen title="Me" showTitleOnPhone>
      <Card>
        <Text variant="heading">{isWeb() ? 'This browser' : 'Paired: this iPhone'}</Text>
        {counts ? <Text tone="muted">{counts}</Text> : <Text tone="muted">Loading…</Text>}
      </Card>
      {libraryId ? <CollectionStats libraryId={libraryId} /> : null}
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
