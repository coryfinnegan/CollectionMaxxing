// Add an item: search the catalog by typing, or "None of these? Add it yourself" for a manual entry.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRef, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CreateItemRequest, ReleaseFormat, WorkKind } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { useItemMutations } from '@/features/item/useItem';
import { SearchView } from '@/features/search/SearchView';
import { localDateString } from '@/lib/dates';
import { friendlyError } from '@/lib/problem';
import { Button, Chip, Sheet, Text, TextField } from '@/ui';
import { formatLabels, formats, kindLabels, kinds } from './labels';

type Props = { libraryId: string; onClose: () => void };

export function AddItemPanel({ libraryId, onClose }: Props) {
  const { create } = useItemMutations(libraryId);
  const key = useRef(newIdempotencyKey());
  // A key replays the first payload it was sent with, so it belongs to one payload:
  // a changed payload needs a fresh key, while retrying the same payload keeps its key.
  const lastPayload = useRef<string | null>(null);
  const [mode, setMode] = useState<'search' | 'manual'>('search');
  const [error, setError] = useState<string | null>(null);

  const submit = (data: CreateItemRequest) => {
    // Acquired is the day it was added (#441): the device stamps its local day, so an
    // evening add stays on today. The server defaults the same day when it is missing.
    const dated: CreateItemRequest = data.acquiredOn ? data : { ...data, acquiredOn: localDateString() };
    const payload = JSON.stringify(dated);
    if (payload !== lastPayload.current) {
      key.current = newIdempotencyKey();
      lastPayload.current = payload;
    }
    setError(null);
    create.mutate(
      { data: dated, idempotencyKey: key.current },
      {
        onSuccess: () => {
          key.current = newIdempotencyKey();
          lastPayload.current = null;
          onClose();
        },
        onError: (e) => setError(friendlyError(e)),
      },
    );
  };

  return (
    <Sheet title="Add an item" onClose={onClose}>
      {mode === 'search' ? (
        <SearchStep onPick={(releaseId) => submit({ releaseId, addedVia: 'search' })} onManual={() => setMode('manual')} />
      ) : null}
      {mode === 'manual' ? <ManualStep busy={create.isPending} onSubmit={submit} /> : null}
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
      {mode === 'search' ? (
        <Button label="None of these? Add it yourself" variant="secondary" onPress={() => setMode('manual')} />
      ) : (
        <Button label="Back to search" variant="secondary" onPress={() => setMode('search')} />
      )}
    </Sheet>
  );
}

function SearchStep({ onPick, onManual }: { onPick: (releaseId: string) => void; onManual: () => void }) {
  return <SearchView onPick={onPick} onManual={onManual} />;
}

function ManualStep({ busy, onSubmit }: { busy: boolean; onSubmit: (data: CreateItemRequest) => void }) {
  const [kind, setKind] = useState<WorkKind>('other');
  const [format, setFormat] = useState<ReleaseFormat>('unknown');
  const [title, setTitle] = useState('');
  const [year, setYear] = useState('');
  const [titleError, setTitleError] = useState<string | null>(null);
  const [yearError, setYearError] = useState<string | null>(null);

  const save = () => {
    const trimmed = title.trim();
    const parsedYear = year.trim() ? Number(year.trim()) : null;
    setTitleError(trimmed ? null : 'Give it a title.');
    const badYear = parsedYear !== null && (!Number.isInteger(parsedYear) || parsedYear < 1000 || parsedYear > 2200);
    setYearError(badYear ? 'Enter a four-digit year, like 1999.' : null);
    if (!trimmed || badYear) return;
    onSubmit({ manual: { kind, title: trimmed, year: parsedYear, format }, addedVia: 'manual' });
  };

  return (
    <View style={styles.step}>
      <Text variant="label">Kind</Text>
      <View style={styles.wrap}>
        {kinds.map((k) => (
          <Chip key={k} label={kindLabels[k]} accessibilityLabel={`Kind ${kindLabels[k]}`} selected={kind === k} onPress={() => setKind(k)} />
        ))}
      </View>
      <TextField label="Title" value={title} onChangeText={setTitle} error={titleError} />
      <TextField label="Year" value={year} onChangeText={setYear} keyboardType="number-pad" maxLength={4} error={yearError} />
      <Text variant="label">Format</Text>
      <View style={styles.wrap}>
        {formats.map((f) => (
          <Chip key={f} label={formatLabels[f]} accessibilityLabel={`Format ${formatLabels[f]}`} selected={format === f} onPress={() => setFormat(f)} />
        ))}
      </View>
      <Button label="Add to library" haptic="commit" onPress={save} busy={busy} />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  step: { gap: theme.spacing.md },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
}));
