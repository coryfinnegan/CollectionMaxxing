// The Mall library design (#357): one nostalgic store per kind, all rendering
// the same items in the same order as the grid. The room canvas up top is the
// browse surface (2.5D on the phone, real-time 3D on the web); the aisle list
// below carries every item in library order for VoiceOver and large text.
// Tapping a cover identifies it without leaving the room.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useMemo, useState } from 'react';
import { FlatList, ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ItemSummaryDto } from '@/api/generated/models';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Button, Card, Chip, Cover, EmptyState, ListRow, Text } from '@/ui';
import { formatLabels } from '../library/labels';
import { anchorSlices, itemsForRoom, mallRoomTabs, presetLabel, roomLabel, type MallListProps } from './anchors';
import { RoomCanvas } from './RoomCanvas';
import { roomPresets, type MallRoomId } from './rooms';

export function MallView({
  header,
  empty,
  items,
  refreshing,
  onRefresh,
  hasNextPage,
  fetchNextPage,
  isFetchingNextPage,
  onOpenItem,
}: MallListProps) {
  const [room, setRoom] = useState<MallRoomId>('mall_hub');
  const [presetIndex, setPresetIndex] = useState(0);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const reduceMotion = useReduceMotion();
  const roomItems = useMemo(() => itemsForRoom(room, items), [room, items]);
  const slices = useMemo(() => anchorSlices(room, items), [room, items]);
  const presets = roomPresets[room];
  const selected = selectedId ? (roomItems.find((item) => item.id === selectedId) ?? null) : null;
  const chooseRoom = (next: MallRoomId) => {
    setRoom(next);
    setPresetIndex(0);
    setSelectedId(null);
  };
  return (
    <FlatList<ItemSummaryDto>
      testID="mall-view"
      data={roomItems}
      keyExtractor={(item) => item.id}
      contentContainerStyle={styles.listContent}
      ListHeaderComponent={
        <View style={styles.header}>
          {header}
          <Text variant="label">Rooms</Text>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
            {mallRoomTabs.map((tab) => (
              <Chip
                key={tab}
                label={roomLabel(tab)}
                accessibilityLabel={`Room ${roomLabel(tab)}`}
                selected={room === tab}
                onPress={() => chooseRoom(tab)}
                testID={`room-${tab}`}
              />
            ))}
          </ScrollView>
          <Text variant="label">Camera</Text>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
            {presets.map((preset, index) => (
              <Chip
                key={preset.name}
                label={presetLabel(preset.name)}
                accessibilityLabel={`Camera ${presetLabel(preset.name)}`}
                selected={presetIndex === index}
                onPress={() => setPresetIndex(index)}
                testID={`preset-${preset.name}`}
              />
            ))}
          </ScrollView>
          <RoomCanvas room={room} presetIndex={presetIndex} slices={slices} selectedId={selectedId} onIdentify={setSelectedId} reduceMotion={reduceMotion} />
          {selected ? (
            <IdentifyCard item={selected} onOpen={() => onOpenItem(selected.id)} onClose={() => setSelectedId(null)} />
          ) : null}
          <Text variant="label">In this room</Text>
        </View>
      }
      ListEmptyComponent={items.length === 0 ? empty : <RoomEmpty />}
      refreshing={refreshing}
      onRefresh={onRefresh}
      onEndReachedThreshold={0.5}
      onEndReached={() => {
        if (hasNextPage && !isFetchingNextPage) fetchNextPage();
      }}
      ListFooterComponent={isFetchingNextPage ? <Text variant="caption" tone="muted">Loading more…</Text> : null}
      renderItem={({ item }) => <AisleRow item={item} onOpenItem={onOpenItem} />}
    />
  );
}

function IdentifyCard({ item, onOpen, onClose }: { item: ItemSummaryDto; onOpen: () => void; onClose: () => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  return (
    <View testID="mall-identify">
      <Card>
        <View style={styles.identifyRow}>
          <Cover title={item.title} uri={item.coverUrl} width={64} />
          <View style={styles.identifyText}>
            <Text variant="heading">{item.title}</Text>
            <Text variant="caption" tone="muted">
              {item.editionVerified ? details : `${details}, edition unverified`}
            </Text>
          </View>
        </View>
        <View style={styles.identifyActions}>
          <Button label="Open item" onPress={onOpen} />
          <Button label="Close" variant="secondary" onPress={onClose} />
        </View>
      </Card>
    </View>
  );
}

function AisleRow({ item, onOpenItem }: { item: ItemSummaryDto; onOpenItem: (id: string) => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  return (
    <ListRow
      title={item.title}
      subtitle={item.editionVerified ? details : `${details}, edition unverified`}
      onPress={() => onOpenItem(item.id)}
      testID={`item-${item.id}`}
    />
  );
}

function RoomEmpty() {
  return <EmptyState title="Nothing in this room." body="Try another room." />;
}

const styles = StyleSheet.create((theme) => ({
  listContent: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  header: { gap: theme.spacing.md },
  chipRow: { gap: theme.spacing.sm },
  identifyRow: { flexDirection: 'row', gap: theme.spacing.md, alignItems: 'center' },
  identifyText: { flex: 1, gap: theme.spacing.xs },
  identifyActions: { flexDirection: 'row', gap: theme.spacing.sm },
}));
