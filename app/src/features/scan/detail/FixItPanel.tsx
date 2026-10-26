// Wrong item? Fix it, from the result detail (docs/06 M4/M5): the other matches to pick from and a search that starts from what was
// read. It uses Review's own calls (the same PATCH and :search), so the card changes the way it does in Review. No picture is involved.
import { useState } from 'react';
import { View } from 'react-native';
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { patchCandidate, searchCandidate } from '@/features/review/api';
import { alternativesOf } from '@/features/review/grouping';
import { contextWorkIdsFor } from '@/features/review/searchContext';
import { useCardsStore } from '../cards/cardsStore';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Sheet, Text, TextField } from '@/ui';

export function FixItPanel({ candidate, sessionId, onClose }: { candidate: CandidateDto; sessionId: string | null; onClose(): void }) {
  const [text, setText] = useState(candidate.visibleText ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const alternatives = alternativesOf(candidate);

  async function run(work: (session: string) => Promise<unknown>) {
    if (!sessionId) return;
    setBusy(true);
    setError(null);
    try {
      await work(sessionId);
      onClose();
    } catch (e) {
      setError(friendlyError(e));
      setBusy(false);
    }
  }

  const search = (session: string) =>
    searchCandidate(session, candidate.id, text.trim(), contextWorkIdsFor(candidate, Object.values(useCardsStore.getState().byId)));

  return (
    <Sheet title="Fix it" onClose={onClose}>
      <View style={{ gap: spacing.sm }}>
        {alternatives.length > 0 ? (
          <>
            <Text variant="label">Pick one</Text>
            {alternatives.map((r: ReleaseDto) => (
              <Button
                key={r.id}
                label={`${r.title}${r.year ? ` (${r.year})` : ''} ${formatLabels[r.format] ?? ''}`.trim()}
                variant="secondary"
                disabled={busy}
                onPress={() => void run((session) => patchCandidate(session, candidate.id, { releaseId: r.id }))}
              />
            ))}
          </>
        ) : null}
        <Text tone="muted">We read this from your picture. Change it to what the title really says.</Text>
        <TextField label="Title" value={text} onChangeText={setText} />
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label="Search" busy={busy} disabled={!text.trim() || !sessionId} onPress={() => void run(search)} />
      </View>
    </Sheet>
  );
}
