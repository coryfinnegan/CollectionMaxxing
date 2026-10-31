// Item edit: status, condition, completeness (games only, #441), location, notes, acquired date, lent-to.
// Saves with If-Match from the ETag the form opened with, and sends only what the person changed. The form holds its
// own snapshot of the item, so a refetch behind it (a refocused browser tab, the refresh after a refused save) never
// swaps the version being edited. A 412 keeps the user's edits on screen and offers Reload, the one thing that takes
// the newer version.
import { useState } from 'react';
import { View } from 'react-native';
import type { ItemCompleteness, ItemCondition, ItemDto, ItemStatus } from '@/api/generated/models';
import { localDateString } from '@/lib/dates';
import { ApiError } from '@/lib/http';
import { useGoBackOr } from '@/lib/navigation';
import { AcquiredField } from './AcquiredField';
import { completenesses, completenessLabels, conditionLabels, conditions } from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { LibraryLoading } from '@/features/onboarding/LibraryLoading';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Card, Chip, EmptyState, ListRow, LoadingState, Screen, Sheet, Text, TextField } from '@/ui';
import { breadcrumb } from './breadcrumb';
import { buildPatch, formFromItem, isEmptyPatch, isValidDate, lentToMissing, type ItemForm } from './etag';
import { StatusChips } from './StatusChips';
import { useItemDetail, useItemMutations } from './useItem';

/** `startAs` opens the form with that status already chosen: Lend… on the item screen opens it as Lent out, ready for a name. */
export function ItemEditScreen({ id, startAs }: { id: string; startAs?: ItemStatus }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  if (isLoading || !libraryId) {
    return (
      <Screen>
        <LibraryLoading label="Loading this item…" />
      </Screen>
    );
  }
  return <EditLoader libraryId={libraryId} id={id} startAs={startAs} />;
}

function EditLoader({ libraryId, id, startAs }: { libraryId: string; id: string; startAs?: ItemStatus }) {
  const { detail, etag, isLoading, error, refetch } = useItemDetail(libraryId, id);
  if (isLoading) {
    return (
      <Screen>
        <LoadingState label="Loading this item…" />
      </Screen>
    );
  }
  // Only a missing item is an error here. A refetch that fails behind an open form leaves the form alone.
  if (!detail || !etag) {
    return (
      <Screen>
        <EmptyState
          title="Couldn't load this item"
          body={friendlyError(error)}
          actionLabel="Try again"
          onAction={() => refetch()}
        />
      </Screen>
    );
  }
  return <EditForm libraryId={libraryId} id={id} detail={detail} etag={etag} refetch={refetch} startAs={startAs} />;
}

type FormProps = {
  libraryId: string;
  id: string;
  startAs?: ItemStatus;
  detail: NonNullable<ReturnType<typeof useItemDetail>['detail']>;
  etag: string;
  refetch: ReturnType<typeof useItemDetail>['refetch'];
};

/** The version of the item the form opened with: what edits are measured against, and the ETag a save carries. */
type Snapshot = { item: ItemDto; etag: string };

function EditForm({ libraryId, id, detail, etag, refetch, startAs }: FormProps) {
  // Taken once, when the form opens. The live `detail` and `etag` move whenever the item is refetched; this does not,
  // except when the person taps Reload on the "someone changed this" card.
  const [snapshot, setSnapshot] = useState<Snapshot>(() => ({ item: detail.item, etag }));
  const leave = useGoBackOr(`/item/${id}`);
  const { locations } = useLocationsList(libraryId);
  const { update } = useItemMutations(libraryId);
  const [form, setForm] = useState<ItemForm>(() => ({ ...formFromItem(snapshot.item), ...(startAs ? { status: startAs } : {}) }));
  const [picking, setPicking] = useState(false);
  const [conflict, setConflict] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dateError, setDateError] = useState<string | null>(null);
  const [lentToError, setLentToError] = useState<string | null>(null);
  const set = <K extends keyof ItemForm>(key: K, value: ItemForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const save = () => {
    setError(null);
    setConflict(false);
    // The native picker can only produce a real date; this guards the web's typed field.
    if (!isValidDate(form.acquiredOn.trim())) {
      setDateError('Use the format 2024-06-02.');
      return;
    }
    setDateError(null);
    if (lentToMissing(form, snapshot.item)) {
      setLentToError('Say who has it.');
      return;
    }
    setLentToError(null);
    const patch = buildPatch({ ...form, acquiredOn: form.acquiredOn.trim() }, snapshot.item, localDateString());
    // Nothing changed, so there is nothing that could write over anyone else's version.
    if (isEmptyPatch(patch)) {
      leave();
      return;
    }
    update.mutate(
      { id, data: patch, etag: snapshot.etag },
      {
        onSuccess: leave,
        onError: (e) => {
          if (e instanceof ApiError && e.status === 412) setConflict(true);
          else setError(friendlyError(e));
        },
      },
    );
  };

  const reload = async () => {
    const result = await refetch();
    // A failed refetch still carries the data it had before; that is not the newer version.
    const fresh = !result.isError && result.data?.status === 200 ? result.data : undefined;
    const freshEtag = fresh?.headers.get('etag');
    if (!fresh || !freshEtag) {
      setError("Couldn't reload. Check your connection and try again.");
      return;
    }
    setSnapshot({ item: fresh.data.item, etag: freshEtag });
    setForm(formFromItem(fresh.data.item));
    setError(null);
    setConflict(false);
  };

  return (
    <Screen subtitle={detail.item.customTitle ?? detail.work.title} keyboardAvoiding>
      {conflict ? (
        <Card>
          <Text tone="danger" accessibilityRole="alert">Someone changed this. Reload?</Text>
          <Text variant="caption" tone="muted">
            Your edits are still here. Reloading replaces them with the latest version.
          </Text>
          <Button label="Reload" variant="secondary" onPress={reload} />
        </Card>
      ) : null}

      <Group label="Status">
        <StatusChips value={form.status} onChange={(s) => set('status', s)} />
      </Group>

      {form.status === 'lent' ? (
        <TextField label="Lent to" value={form.lentTo} onChangeText={(v) => set('lentTo', v)} placeholder="Sam" error={lentToError} />
      ) : null}

      <Group label="Condition">
        <Chip label="Not set" accessibilityLabel="Condition not set" selected={form.condition === null} onPress={() => set('condition', null)} />
        {conditions.map((c: Exclude<ItemCondition, null>) => (
          <Chip key={c} label={conditionLabels[c]} accessibilityLabel={`Condition ${conditionLabels[c]}`} selected={form.condition === c} onPress={() => set('condition', c)} />
        ))}
      </Group>

      {detail.work.kind === 'game' ? (
        <Group label="Completeness">
          <Chip label="Not set" accessibilityLabel="Completeness not set" selected={form.completeness === null} onPress={() => set('completeness', null)} />
          {completenesses.map((c: Exclude<ItemCompleteness, null>) => (
            <Chip key={c} label={completenessLabels[c]} accessibilityLabel={`Completeness ${completenessLabels[c]}`} selected={form.completeness === c} onPress={() => set('completeness', c)} />
          ))}
        </Group>
      ) : null}

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

      <AcquiredField value={form.acquiredOn} onChange={(v) => set('acquiredOn', v)} error={dateError} />
      <TextField label="Notes" value={form.notes} onChangeText={(v) => set('notes', v)} multiline />

      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}

      <View style={{ flexDirection: 'row', gap: spacing.sm }}>
        <Button label="Save changes" haptic="commit" onPress={save} busy={update.isPending} />
        <Button label="Cancel" variant="secondary" onPress={leave} />
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
