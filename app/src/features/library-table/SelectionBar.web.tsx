// The bar under the table: "N selected" with Move to, Set status, Add tag and Delete.
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ItemStatus, LocationDto, TagDto } from '@/api/generated/models';
import { statuses, statusLabels } from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { Button, Chip, ListRow, Sheet, Text } from '@/ui';

type Props = {
  /** Ids that the next bulk action will send (already capped). */
  count: number;
  selectedCount: number;
  capMessage?: string;
  busy: boolean;
  locations: LocationDto[];
  tags: TagDto[];
  onMove: (locationId: string | null) => void;
  onStatus: (status: ItemStatus) => void;
  onTag: (tagId: string) => void;
  onDelete: () => void;
  onKeepFirst: () => void;
  onClear: () => void;
};

type Panel = 'move' | 'status' | 'tag' | 'delete' | null;

const items = (n: number) => `${n} ${n === 1 ? 'item' : 'items'}`;

export function SelectionBar(props: Props) {
  const { count, selectedCount, capMessage, busy, locations, tags } = props;
  const [panel, setPanel] = useState<Panel>(null);
  const run = (action: () => void) => {
    setPanel(null);
    action();
  };

  return (
    <View accessibilityRole="toolbar" accessibilityLabel="Selected items" style={styles.bar}>
      <View style={styles.actions}>
        <Text variant="label" accessibilityRole="header" style={styles.count}>
          {selectedCount} selected
        </Text>
        <Button label="Move to" variant="secondary" disabled={busy} onPress={() => setPanel('move')} />
        <Button label="Set status" variant="secondary" disabled={busy} onPress={() => setPanel('status')} />
        <Button label="Add tag" variant="secondary" disabled={busy} onPress={() => setPanel('tag')} />
        <Button label="Delete" variant="danger" disabled={busy} onPress={() => setPanel('delete')} />
        <Button label="Clear selection" variant="secondary" onPress={props.onClear} />
      </View>

      {capMessage ? (
        <View style={styles.cap}>
          <Text tone="muted" accessibilityRole="alert">
            {capMessage}
          </Text>
          <View style={styles.row}>
            <Button label={`Select only the first ${count}`} variant="secondary" onPress={props.onKeepFirst} />
          </View>
        </View>
      ) : null}

      {panel === 'move' ? (
        <Sheet title={`Move ${items(count)} to`} onClose={() => setPanel(null)}>
          <ListRow title="No location" testID="move-to-none" onPress={() => run(() => props.onMove(null))} />
          {flattenTree(locations).map(({ location, depth }) => (
            <ListRow
              key={location.id}
              title={location.name}
              depth={depth}
              testID={`move-to-${location.id}`}
              onPress={() => run(() => props.onMove(location.id))}
            />
          ))}
        </Sheet>
      ) : null}

      {panel === 'status' ? (
        <Sheet title={`Set status of ${items(count)}`} onClose={() => setPanel(null)}>
          <View style={styles.wrap}>
            {statuses.map((status) => (
              <Chip key={status} label={statusLabels[status]} accessibilityLabel={`Set status to ${statusLabels[status]}`} onPress={() => run(() => props.onStatus(status))} />
            ))}
          </View>
        </Sheet>
      ) : null}

      {panel === 'tag' ? (
        <Sheet title={`Add a tag to ${items(count)}`} onClose={() => setPanel(null)}>
          {tags.length === 0 ? <Text tone="muted">There are no tags yet.</Text> : null}
          <View style={styles.wrap}>
            {tags.map((tag) => (
              <Chip key={tag.id} label={tag.name} accessibilityLabel={`Add tag ${tag.name}`} onPress={() => run(() => props.onTag(tag.id))} />
            ))}
          </View>
        </Sheet>
      ) : null}

      {panel === 'delete' ? (
        <Sheet title={`Delete ${items(count)}?`} onClose={() => setPanel(null)}>
          <Text tone="muted">They leave your library. You can restore one from its page for a while.</Text>
          <View style={styles.confirm}>
            <Button label={`Delete ${items(count)}`} variant="danger" onPress={() => run(props.onDelete)} />
            <Button label="Keep them" variant="secondary" onPress={() => setPanel(null)} />
          </View>
        </Sheet>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: {
    gap: theme.spacing.md,
    padding: theme.spacing.md,
    borderRadius: theme.radius.lg,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
  actions: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: theme.spacing.sm },
  count: { marginRight: theme.spacing.sm },
  cap: { gap: theme.spacing.sm },
  row: { flexDirection: 'row' },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  confirm: { flexDirection: 'row', gap: theme.spacing.sm },
}));
