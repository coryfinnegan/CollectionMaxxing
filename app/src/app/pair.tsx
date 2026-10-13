import { useRouter } from 'expo-router';
import { Button, Card, Screen, Text } from '@/ui';

export default function Pair() {
  const router = useRouter();
  return (
    <Screen title="Pair with your server" subtitle="Pair this iPhone with your library.">
      <Card>
        <Text>1. On your computer, open your server address.</Text>
        <Text>2. Go to Settings, then Devices, then Add a device.</Text>
        <Text>3. Scan the QR code shown there.</Text>
        <Text variant="caption" tone="muted">
          Pairing arrives in a later task. Until then you can open the library without it.
        </Text>
      </Card>
      <Button label="Open the library" onPress={() => router.replace('/library')} />
    </Screen>
  );
}
