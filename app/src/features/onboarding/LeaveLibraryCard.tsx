// Settings -> Library, for everyone who isn't an owner: leave this library. Only the membership ends; the account stays.
import { useState } from 'react';
import { Button, Card, Text } from '@/ui';
import { leaveLibraryError, leaveLibraryWarning } from './copy';
import { useLeaveLibrary } from './hooks';
import { useActiveLibraryId, useLibraries } from './useLibraries';

export function LeaveLibraryCard() {
  const { libraryId } = useActiveLibraryId();
  const { libraries } = useLibraries();
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const leave = useLeaveLibrary();
  const library = libraries.find((l) => l.id === libraryId);
  if (!library || library.role === 'owner') return null;

  return (
    <Card>
      <Text variant="heading">Library</Text>
      {confirming ? (
        <>
          <Text>{leaveLibraryWarning(library.name)}</Text>
          {error ? (
            <Text tone="danger" accessibilityRole="alert">
              {error}
            </Text>
          ) : null}
          <Button
            label="Leave library"
            variant="danger"
            busy={leave.isPending}
            onPress={() => {
              setError(null);
              leave.mutate(library.id, { onError: (e) => setError(leaveLibraryError(e)) });
            }}
          />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(false)} />
        </>
      ) : (
        <Button label="Leave library" variant="secondary" onPress={() => setConfirming(true)} />
      )}
    </Card>
  );
}
