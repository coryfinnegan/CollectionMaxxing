// Pushed Me screens are tab screens without a stack header, so each one carries its own way back (#269).
import { useRouter } from 'expo-router';
import { Pressable } from 'react-native';
import { Text } from '@/ui';

export function BackButton() {
  const router = useRouter();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel="Back"
      onPress={() => router.back()}
      style={{ alignSelf: 'flex-start', minWidth: 44, minHeight: 44, justifyContent: 'center' }}
    >
      <Text variant="label" tone="scan">
        ‹ Back
      </Text>
    </Pressable>
  );
}
