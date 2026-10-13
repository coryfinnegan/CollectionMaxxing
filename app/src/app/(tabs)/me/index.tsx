import { type Href, useRouter } from 'expo-router';
import { Card, ListRow, Screen, Text } from '@/ui';

const links: { title: string; href: Href }[] = [
  { title: 'Locations', href: '/me/locations' },
  { title: 'Export my data', href: '/me/export' },
  { title: 'Devices and browsers', href: '/me/devices' },
  { title: 'Delete account', href: '/me/delete' },
];

export default function Me() {
  const router = useRouter();
  return (
    <Screen title="Me">
      <Card>
        <Text variant="caption" tone="muted">
          Your name and library details appear here once pairing is built.
        </Text>
      </Card>
      <Card>
        {links.map((link) => (
          <ListRow key={link.title} title={link.title} onPress={() => router.push(link.href)} trailing={<Text tone="muted">›</Text>} />
        ))}
      </Card>
    </Screen>
  );
}
