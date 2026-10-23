// "Create your library": a name, then you're its Owner and land in it.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { Button, Card, Screen, Text, TextField } from '@/ui';
import { createLibraryError } from './copy';
import { useCreateLibrary } from './hooks';
import { useLibraries } from './useLibraries';
import { useAccountName } from './WelcomeScreen';

export function CreateLibraryScreen() {
  const router = useRouter();
  const name = useAccountName();
  const { libraries, loaded } = useLibraries();
  const [value, setValue] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const create = useCreateLibrary();

  const text = value ?? (name ? `${name}'s library` : 'My library');
  const submit = () => {
    if (!text.trim()) {
      setError('Give your library a name.');
      return;
    }
    setError(null);
    create.mutate({ name: text.trim() }, { onSuccess: () => router.replace('/library'), onError: (e) => setError(createLibraryError(e)) });
  };

  return (
    <Screen title="Create your library">
      <Card>
        <TextField label="Library name" value={text} onChangeText={setValue} maxLength={80} autoComplete="off" onSubmitEditing={submit} />
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label="Create library" busy={create.isPending} onPress={submit} />
        <Button label="Back" variant="secondary" onPress={() => router.replace(loaded && libraries.length > 0 ? '/library' : '/welcome')} />
      </Card>
    </Screen>
  );
}
