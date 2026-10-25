// Pushed pages carry their own way back on the web, where page stacks have no
// header; on the phone the stack header already has one (#269, #303).
import { useRouter } from 'expo-router';
import { Platform, Pressable } from 'react-native';
import { Text } from '@/ui';

export function BackButton() {
  const router = useRouter();
  if (Platform.OS !== 'web') return null;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel="Back"
      onPress={() => router.back()}
      style={{ alignSelf: 'flex-start', minWidth: 44, minHeight: 44, justifyContent: 'center' }}
    >
      <Text variant="label" tone="accent">
        ‹ Back
      </Text>
    </Pressable>
  );
}
