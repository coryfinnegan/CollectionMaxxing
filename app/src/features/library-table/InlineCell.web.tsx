// Inline edit of one cell (location or status). Opening the cell loads the item's ETag; choosing a value
// sends PATCH with If-Match. A 412 reloads the row and says so. Choosing Lent out asks who has it first: a lend needs a name.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRef, useState } from 'react';
import { Pressable, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { getItem, updateItem } from '@/api/generated/items/items';
import type { ItemStatus, ItemSummaryDto, UpdateItemRequest } from '@/api/generated/models';
import { ifMatchHeader } from '@/features/item/etag';
import { useInvalidateLibrary } from '@/features/item/useItem';
import { localDateString } from '@/lib/dates';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { Button, Text, TextField } from '@/ui';
import { needsBorrower, statusPatch } from './statusPatch';

export type CellOption = { value: string; label: string; depth?: number };

type Props = {
  libraryId: string;
  item: ItemSummaryDto;
  field: 'status' | 'location';
  /** What the cell shows now, in words. */
  current: string;
  options: CellOption[];
  onNotice: (text: string) => void;
};

const patchFor = (field: Props['field'], item: ItemSummaryDto, value: string): UpdateItemRequest =>
  field === 'status'
    ? statusPatch(item.status, value as ItemStatus, localDateString())
    : { locationId: value === '' ? null : value };

const startsLend = (field: Props['field'], item: ItemSummaryDto, value: string) =>
  field === 'status' && needsBorrower(item.status, value as ItemStatus);

export function InlineCell({ libraryId, item, field, current, options, onNotice }: Props) {
  const invalidate = useInvalidateLibrary();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [asking, setAsking] = useState(false);
  const [lentTo, setLentTo] = useState('');
  const etag = useRef<Promise<string | undefined> | null>(null);
  const noun = field === 'status' ? 'status' : 'location';

  const toggle = () => {
    if (!open) {
      // Load the ETag as the editor opens, so If-Match reflects what the person was looking at.
      etag.current = getItem(libraryId, item.id)
        .then((response) => response.headers.get('etag') ?? undefined)
        .catch(() => undefined);
    }
    setOpen(!open);
  };

  const send = async (patch: UpdateItemRequest) => {
    setBusy(true);
    try {
      const tag = await etag.current;
      if (!tag) {
        onNotice(`Couldn't load "${item.title}" to change its ${noun}. Reload the list and try again.`);
        return;
      }
      await updateItem(libraryId, item.id, patch, { headers: ifMatchHeader(tag) });
      onNotice('');
    } catch (error) {
      if (error instanceof ApiError && error.status === 412) {
        // The row reloads below. The API doesn't say who changed it, so the copy says "someone else".
        onNotice(`Someone else changed "${item.title}" while you were editing. The row now shows the latest, so choose the ${noun} again.`);
      } else {
        onNotice(friendlyError(error));
      }
    } finally {
      etag.current = null;
      setLentTo('');
      setBusy(false);
      await invalidate(libraryId);
    }
  };

  const choose = async (value: string) => {
    setOpen(false);
    if (startsLend(field, item, value)) {
      // The ETag loaded when the menu opened stays for the save below.
      setAsking(true);
      return;
    }
    await send(patchFor(field, item, value));
  };

  const lend = async () => {
    if (!lentTo.trim()) return;
    setAsking(false);
    await send(statusPatch(item.status, 'lent', localDateString(), lentTo));
  };

  const cancelLend = () => {
    setAsking(false);
    setLentTo('');
    etag.current = null;
  };

  if (asking) {
    return (
      <View style={styles.root}>
        <TextField
          label="Lent to"
          accessibilityLabel={`Who has ${item.title}`}
          value={lentTo}
          onChangeText={setLentTo}
          placeholder="Sam"
          autoFocus
          returnKeyType="done"
          onSubmitEditing={lend}
        />
        <View style={styles.actions}>
          <Button label="Lend" accessibilityLabel={`Lend ${item.title}`} disabled={!lentTo.trim()} onPress={lend} />
          <Button label="Cancel" accessibilityLabel={`Cancel lending ${item.title}`} variant="secondary" onPress={cancelLend} />
        </View>
      </View>
    );
  }

  return (
    <View style={styles.root}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`Change ${noun} of ${item.title}, now ${current}`}
        accessibilityState={{ expanded: open, busy }}
        disabled={busy}
        onPress={toggle}
        style={styles.trigger}
      >
        <Text variant="body" numberOfLines={1} style={styles.current}>
          {current}
        </Text>
      </Pressable>
      {open ? (
        <View style={styles.menu}>
          {options.map((option) => (
            <Pressable
              key={option.value}
              accessibilityRole="menuitem"
              accessibilityLabel={`Set ${noun} of ${item.title} to ${option.label}`}
              onPress={() => choose(option.value)}
              style={styles.option(option.depth ?? 0)}
            >
              <Text variant="label">{option.label}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { gap: theme.spacing.xs },
  actions: { flexDirection: 'row', gap: theme.spacing.sm },
  trigger: { minHeight: 44, justifyContent: 'center' },
  current: { color: theme.colors.ink },
  menu: {
    gap: theme.spacing.xs,
    padding: theme.spacing.xs,
    borderRadius: theme.radius.md,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
  option: (depth: number) => ({ paddingVertical: theme.spacing.xs, paddingLeft: theme.spacing.sm + depth * theme.spacing.md }),
}));
