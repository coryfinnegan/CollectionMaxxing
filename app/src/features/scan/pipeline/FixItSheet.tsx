// Fix it, from a card the catalog could not find: a search that starts from what the phone read. The search is a text query
// to the catalog (the same call Review uses); nothing is added, and the picture is not involved.
import { useState } from 'react';
import { View } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { searchForCandidate } from '@/api/generated/scanning/scanning';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Sheet, Text, TextField } from '@/ui';
import { useCardsStore } from '../cards/cardsStore';
import { announce } from '../session/sessionApi';

/** Searches again with this text; the card updates from the answer. */
export async function searchAgain(sessionId: string, candidate: CandidateDto, text: string): Promise<void> {
  const response = await searchForCandidate(sessionId, candidate.id, { text });
  if (response.status !== 200) throw new ApiError(response.status, {});
  announce(useCardsStore.getState().merge([response.data]));
}

export function FixItSheet({ candidate, sessionId, onClose }: { candidate: CandidateDto; sessionId: string | null; onClose(): void }) {
  const [text, setText] = useState(candidate.visibleText ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function search() {
    if (!sessionId) return;
    setBusy(true);
    setError(null);
    try {
      await searchAgain(sessionId, candidate, text.trim());
      onClose();
    } catch (e) {
      setError(friendlyError(e));
      setBusy(false);
    }
  }

  return (
    <Sheet title="Fix it" onClose={onClose}>
      <View style={{ gap: spacing.sm }}>
        <Text tone="muted">We read this from your picture. Change it to what the title really says.</Text>
        <TextField label="Title" value={text} onChangeText={setText} />
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label="Search" busy={busy} disabled={!text.trim() || !sessionId} onPress={() => void search()} />
      </View>
    </Sheet>
  );
}
