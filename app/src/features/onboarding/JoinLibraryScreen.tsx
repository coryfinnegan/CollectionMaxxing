// "Join a library": paste a link or type a code, see who invited you, then join.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import type { LibraryRole } from '@/api/generated/models';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text, TextField } from '@/ui';
import { inviteError, invalidInviteCopy, joinPrompt } from './copy';
import { useAcceptInvite, useInvitePreview } from './hooks';
import { parseInvite } from './inviteInput';
import { useLibraries } from './useLibraries';

export const roleLabel: Record<LibraryRole, string> = { owner: 'Owner', editor: 'Editor', viewer: 'Viewer' };

export function JoinLibraryScreen() {
  const router = useRouter();
  const colors = useColors();
  const { libraries, loaded } = useLibraries();
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const preview = useInvitePreview();
  const accept = useAcceptInvite();
  const invite = parseInvite(text);

  const edit = (next: string) => {
    setText(next);
    setError(null);
    preview.reset();
  };
  const check = () => {
    if (!invite) {
      setError(invalidInviteCopy);
      return;
    }
    setError(null);
    preview.mutate({ invite }, { onError: (e) => setError(inviteError(e)) });
  };
  const join = () => {
    if (!invite) return;
    setError(null);
    accept.mutate({ invite }, { onSuccess: () => router.replace('/library'), onError: (e) => setError(inviteError(e)) });
  };

  const found = preview.data;
  return (
    <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title="Join a library" showTitleOnPhone subtitle={joinPrompt}>
        <Card>
          <TextField
            label="Invite link or code"
            value={text}
            onChangeText={edit}
            autoCapitalize="none"
            autoCorrect={false}
            autoComplete="off"
            onSubmitEditing={found ? join : check}
          />
          {found ? <Text variant="heading">{`Join ${found.ownerName}'s library as ${roleLabel[found.role]}`}</Text> : null}
          {error ? (
            <Text tone="danger" accessibilityRole="alert">
              {error}
            </Text>
          ) : null}
          {found ? (
            <Button label="Join" haptic="commit" busy={accept.isPending} onPress={join} />
          ) : (
            <Button label="Check invite" busy={preview.isPending} disabled={!text.trim()} onPress={check} />
          )}
          <Button label="Back" variant="secondary" onPress={() => router.replace(loaded && libraries.length > 0 ? '/library' : '/welcome')} />
        </Card>
      </Screen>
    </SafeAreaView>
  );
}
