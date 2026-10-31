// Inline edit of one cell (location or status). Opening the cell loads the item's ETag; choosing a value
// sends PATCH with If-Match. A 412 reloads the row and says so.
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
import { Text } from '@/ui';
import { statusPatch } from './statusPatch';

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

export function InlineCell({ libraryId, item, field, current, options, onNotice }: Props) {
  const invalidate = useInvalidateLibrary();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
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

  const choose = async (value: string) => {
    setOpen(false);
    setBusy(true);
    try {
      const tag = await etag.current;
      if (!tag) {
        onNotice(`Couldn't load "${item.title}" to change its ${noun}. Reload the list and try again.`);
        return;
      }
      await updateItem(libraryId, item.id, patchFor(field, item, value), { headers: ifMatchHeader(tag) });
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
      setBusy(false);
      await invalidate(libraryId);
    }
  };

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
