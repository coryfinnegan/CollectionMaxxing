// Item edit: status, condition, completeness, location, notes, acquired date, lent-to.
// Saves with If-Match from the loaded ETag. A 412 keeps the user's edits on screen and offers Reload.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';
import type { ItemCompleteness, ItemCondition, ItemStatus } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import {
  completenesses,
  completenessLabels,
  conditionLabels,
  conditions,
  statuses,
  statusLabels,
} from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Card, Chip, EmptyState, ListRow, Screen, Sheet, Text, TextField } from '@/ui';
import { breadcrumb } from './breadcrumb';
import { buildPatch, formFromItem, isValidDate, type ItemForm } from './etag';
import { useItemDetail, useItemMutations } from './useItem';

export function ItemEditScreen({ id }: { id: string }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  if (isLoading || !libraryId) {
    return (
      <Screen title="Edit item">
        <Text tone="muted">Loading this item…</Text>
      </Screen>
    );
  }
  return <EditLoader libraryId={libraryId} id={id} />;
}

function EditLoader({ libraryId, id }: { libraryId: string; id: string }) {
  const { detail, etag, isLoading, isError, error, refetch } = useItemDetail(libraryId, id);
  if (isLoading) {
    return (
      <Screen title="Edit item">
        <Text tone="muted">Loading this item…</Text>
      </Screen>
    );
  }
  if (isError || !detail || !etag) {
    return (
      <Screen title="Edit item">
        <EmptyState
          title="Couldn't load this item"
          body={friendlyError(error)}
          actionLabel="Try again"
          onAction={() => refetch()}
        />
      </Screen>
    );
  }
  return <EditForm libraryId={libraryId} id={id} detail={detail} etag={etag} refetch={refetch} />;
}

type FormProps = {
  libraryId: string;
  id: string;
  detail: NonNullable<ReturnType<typeof useItemDetail>['detail']>;
  etag: string;
  refetch: ReturnType<typeof useItemDetail>['refetch'];
};

function EditForm({ libraryId, id, detail, etag, refetch }: FormProps) {
  const router = useRouter();
  const { locations } = useLocationsList(libraryId);
  const { update } = useItemMutations(libraryId);
  const [form, setForm] = useState<ItemForm>(() => formFromItem(detail.item));
  const [picking, setPicking] = useState(false);
  const [conflict, setConflict] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dateError, setDateError] = useState<string | null>(null);
  const set = <K extends keyof ItemForm>(key: K, value: ItemForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const save = () => {
    setError(null);
    setConflict(false);
    if (!isValidDate(form.acquiredOn.trim())) {
      setDateError('Use the format 2024-06-02.');
      return;
    }
    setDateError(null);
    const today = new Date().toISOString().slice(0, 10);
    update.mutate(
      { id, data: buildPatch({ ...form, acquiredOn: form.acquiredOn.trim() }, detail.item, today), etag },
      {
        onSuccess: () => router.back(),
        onError: (e) => {
          if (e instanceof ApiError && e.status === 412) setConflict(true);
          else setError(friendlyError(e));
        },
      },
    );
  };

  const reload = async () => {
    const result = await refetch();
    const fresh = result.data?.status === 200 ? result.data.data : undefined;
    if (fresh) setForm(formFromItem(fresh.item));
    setConflict(false);
  };

  return (
    <Screen title="Edit item" subtitle={detail.item.customTitle ?? detail.work.title}>
      {conflict ? (
        <Card>
          <Text accessibilityRole="alert">Someone changed this. Reload?</Text>
          <Text variant="caption" tone="muted">
            Your edits are still here. Reloading replaces them with the latest version.
          </Text>
          <Button label="Reload" variant="secondary" onPress={reload} />
        </Card>
      ) : null}

      <Group label="Status">
        {statuses.map((s: ItemStatus) => (
          <Chip key={s} label={statusLabels[s]} accessibilityLabel={`Status ${statusLabels[s]}`} selected={form.status === s} onPress={() => set('status', s)} />
        ))}
      </Group>

      {form.status === 'lent' ? (
        <TextField label="Lent to" value={form.lentTo} onChangeText={(v) => set('lentTo', v)} placeholder="Sam" />
      ) : null}

      <Group label="Condition">
        <Chip label="Not set" accessibilityLabel="Condition not set" selected={form.condition === null} onPress={() => set('condition', null)} />
        {conditions.map((c: Exclude<ItemCondition, null>) => (
          <Chip key={c} label={conditionLabels[c]} accessibilityLabel={`Condition ${conditionLabels[c]}`} selected={form.condition === c} onPress={() => set('condition', c)} />
        ))}
      </Group>

      <Group label="Completeness">
        <Chip label="Not set" accessibilityLabel="Completeness not set" selected={form.completeness === null} onPress={() => set('completeness', null)} />
        {completenesses.map((c: Exclude<ItemCompleteness, null>) => (
          <Chip key={c} label={completenessLabels[c]} accessibilityLabel={`Completeness ${completenessLabels[c]}`} selected={form.completeness === c} onPress={() => set('completeness', c)} />
        ))}
      </Group>

      <View style={{ gap: spacing.sm }}>
        <Text variant="label">Location</Text>
        <Button
          label={breadcrumb(locations, form.locationId)}
          accessibilityLabel={`Location: ${breadcrumb(locations, form.locationId)}. Change`}
          variant="secondary"
          onPress={() => setPicking(!picking)}
        />
        {picking ? (
          <Sheet title="Pick a location" onClose={() => setPicking(false)}>
            <ListRow
              title="Not placed"
              onPress={() => {
                set('locationId', null);
                setPicking(false);
              }}
            />
            {flattenTree(locations).map(({ location, depth }) => (
              <ListRow
                key={location.id}
                title={location.name}
                depth={depth}
                onPress={() => {
                  set('locationId', location.id);
                  setPicking(false);
                }}
              />
            ))}
          </Sheet>
        ) : null}
      </View>

      <TextField label="Acquired" value={form.acquiredOn} onChangeText={(v) => set('acquiredOn', v)} placeholder="2024-06-02" error={dateError} />
      <TextField label="Notes" value={form.notes} onChangeText={(v) => set('notes', v)} multiline />

      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}

      <View style={{ flexDirection: 'row', gap: spacing.sm }}>
        <Button label="Save changes" onPress={save} busy={update.isPending} />
        <Button label="Cancel" variant="secondary" onPress={() => router.back()} />
      </View>
    </Screen>
  );
}

function Group({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <View style={{ gap: spacing.sm }}>
      <Text variant="label">{label}</Text>
      <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>{children}</View>
    </View>
  );
}
