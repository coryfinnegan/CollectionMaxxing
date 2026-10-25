// Add an item: search the catalog by typing, or "None of these? Add it yourself" for a manual entry.
import { useRef, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useSearchCatalog } from '@/api/generated/catalog/catalog';
import type { CreateItemRequest, ReleaseFormat, WorkKind } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { useItemMutations } from '@/features/item/useItem';
import { friendlyError } from '@/lib/problem';
import { Button, Chip, ListRow, Sheet, Text, TextField } from '@/ui';
import { formatLabels, formats, kindLabels, kinds } from './labels';
import { useDebounced } from './useItems';

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
    const payload = JSON.stringify(data);
    if (payload !== lastPayload.current) {
      key.current = newIdempotencyKey();
      lastPayload.current = payload;
    }
    setError(null);
    create.mutate(
      { data, idempotencyKey: key.current },
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
      {mode === 'search' ? <SearchStep onPick={(releaseId) => submit({ releaseId, addedVia: 'search' })} /> : null}
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

function SearchStep({ onPick }: { onPick: (releaseId: string) => void }) {
  const [text, setText] = useState('');
  const q = useDebounced(text.trim());
  const search = useSearchCatalog({ q }, { query: { enabled: q.length >= 2 } });
  const results = search.data?.status === 200 ? search.data.data.items : [];
  return (
    <View style={styles.step}>
      <TextField
        label="Search the catalog"
        value={text}
        onChangeText={setText}
        placeholder="Title, author or barcode"
        autoCapitalize="none"
        autoCorrect={false}
      />
      {search.isError ? (
        <Text tone="danger" accessibilityRole="alert">
          {"Couldn't search the catalog. Check your connection, or add it yourself below."}
        </Text>
      ) : q.length >= 2 && !search.isLoading && results.length === 0 ? (
        <Text variant="caption" tone="muted">No matches yet.</Text>
      ) : null}
      {results.flatMap((result) =>
        result.releases.map((release) => {
          const detail = [formatLabels[release.format], release.year].filter(Boolean).join(' · ');
          return (
            <ListRow
              key={release.id}
              title={result.work.title}
              subtitle={detail}
              onPress={() => onPick(release.id)}
              testID={`result-${release.id}`}
            />
          );
        }),
      )}
    </View>
  );
}

function ManualStep({ busy, onSubmit }: { busy: boolean; onSubmit: (data: CreateItemRequest) => void }) {
  const [kind, setKind] = useState<WorkKind>('other');
  const [format, setFormat] = useState<ReleaseFormat>('other');
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
