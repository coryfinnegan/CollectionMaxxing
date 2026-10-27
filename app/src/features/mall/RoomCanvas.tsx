// The Mall room on the phone (#357): a 2.5D walk down the aisle. The room wears
// its Blender palette, each anchor is a card of tappable covers, and the camera
// presets glide the carousel to that end of the room. Tapping a cover
// identifies the item below; the aisle list under the room carries the same
// items in the same order for VoiceOver and large text.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useRef } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ItemSummaryDto } from '@/api/generated/models';
import { mallPalette, spacing, type MallRoomPalette } from '@/theme/tokens';
import { Cover, Text } from '@/ui';
import { formatLabels } from '../library/labels';
import { roomLabel, sectionLabel, type AnchorSlice } from './anchors';
import type { MallRoomId } from './rooms';

export type RoomCanvasProps = {
  room: MallRoomId;
  presetIndex: number;
  slices: AnchorSlice[];
  selectedId: string | null;
  onIdentify: (id: string) => void;
  reduceMotion: boolean;
};

const paletteFor: Record<MallRoomId, MallRoomPalette> = {
  video_store: 'video',
  toy_store: 'toy',
  bookstore: 'book',
  mall_hub: 'hub',
};

const CARD_WIDTH = 232;
const CARD_STRIDE = CARD_WIDTH + spacing.md;
const COVER_WIDTH = 48;

export function RoomCanvas({ room, presetIndex, slices, selectedId, onIdentify, reduceMotion }: RoomCanvasProps) {
  const palette = mallPalette[paletteFor[room]];
  const scrollRef = useRef<ScrollView>(null);
  useEffect(() => {
    scrollRef.current?.scrollTo?.({ x: presetIndex * CARD_STRIDE, animated: !reduceMotion });
  }, [presetIndex, reduceMotion, room]);
  return (
    <View style={styles.room(palette.wall)} testID="mall-room">
      <Text variant="heading" style={styles.sign(palette.sign)}>
        {roomLabel(room)}
      </Text>
      <ScrollView
        ref={scrollRef}
        horizontal
        showsHorizontalScrollIndicator={false}
        snapToInterval={CARD_STRIDE}
        decelerationRate="fast"
        contentContainerStyle={styles.carousel}
      >
        {slices.map((slice) => (
          <AnchorCard key={slice.name} slice={slice} selectedId={selectedId} onIdentify={onIdentify} />
        ))}
      </ScrollView>
      <View style={styles.fixture(palette.fixture, palette.trim)} />
    </View>
  );
}

function AnchorCard({ slice, selectedId, onIdentify }: { slice: AnchorSlice; selectedId: string | null; onIdentify: (id: string) => void }) {
  return (
    <View style={styles.card} testID={`anchor-${slice.name}`}>
      <Text variant="label">{sectionLabel(slice.section)}</Text>
      {slice.items.length === 0 ? (
        <Text variant="caption" tone="muted">
          Nothing shelved here.
        </Text>
      ) : (
        <View style={styles.covers}>
          {slice.items.map((item) => (
            <AnchorCover key={item.id} item={item} selected={item.id === selectedId} onIdentify={onIdentify} />
          ))}
        </View>
      )}
    </View>
  );
}

function AnchorCover({ item, selected, onIdentify }: { item: ItemSummaryDto; selected: boolean; onIdentify: (id: string) => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={item.editionVerified ? `${item.title}, ${details}` : `${item.title}, ${details}, edition unverified`}
      onPress={() => onIdentify(item.id)}
      testID={`cover-${item.id}`}
      style={styles.coverWrap(selected)}
    >
      <Cover title={item.title} uri={item.coverUrl} width={COVER_WIDTH} />
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  room: (wall: string) => ({
    backgroundColor: wall,
    borderRadius: theme.radius.lg,
    overflow: 'hidden',
    gap: theme.spacing.md,
    paddingTop: theme.spacing.md,
  }),
  sign: (color: string) => ({ color, textAlign: 'center' }),
  carousel: { gap: theme.spacing.md, paddingHorizontal: theme.spacing.lg },
  card: { width: CARD_WIDTH, backgroundColor: theme.colors.surface, borderRadius: theme.radius.md, padding: theme.spacing.sm, gap: theme.spacing.xs },
  covers: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.xs },
  coverWrap: (selected: boolean) => ({
    borderWidth: 2,
    borderRadius: theme.radius.sm,
    borderColor: selected ? theme.colors.accent : 'transparent',
  }),
  fixture: (floor: string, trim: string) => ({ height: 56, backgroundColor: floor, borderTopWidth: 4, borderTopColor: trim }),
}));
