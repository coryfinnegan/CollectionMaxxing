// "Create your library": a name, then you're its Owner and land in it.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text, TextField } from '@/ui';
import { createLibraryError } from './copy';
import { useCreateLibrary } from './hooks';
import { useLibraries } from './useLibraries';
import { useAccount } from './WelcomeScreen';

export function CreateLibraryScreen() {
  const router = useRouter();
  const colors = useColors();
  const account = useAccount();
  const { libraries, loaded } = useLibraries();
  const [value, setValue] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const create = useCreateLibrary();

  const text = value ?? (account ? `${account.name}'s library` : '');
  const submit = () => {
    if (!text.trim()) {
      setError('Give your library a name.');
      return;
    }
    setError(null);
    create.mutate({ name: text.trim() }, { onSuccess: () => router.replace('/library'), onError: (e) => setError(createLibraryError(e)) });
  };

  return (
    <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title="Create your library" showTitleOnPhone keyboardAvoiding>
        <Card>
          <TextField label="Library name" value={text} onChangeText={setValue} maxLength={80} autoComplete="off" onSubmitEditing={submit} />
          {error ? (
            <Text tone="danger" accessibilityRole="alert">
              {error}
            </Text>
          ) : null}
          <Button label="Create library" haptic="commit" busy={create.isPending} onPress={submit} />
          <Button label="Back" variant="secondary" onPress={() => router.replace(loaded && libraries.length > 0 ? '/library' : '/welcome')} />
        </Card>
      </Screen>
    </SafeAreaView>
  );
}
