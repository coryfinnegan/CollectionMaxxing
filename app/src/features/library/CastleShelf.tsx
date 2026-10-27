// The library as castle shelves (#357): the same items in the same order as the
// grid, dealt onto stone ledges in shelf rows. Shelves are consecutive runs of
// the grid order — never regrouped — so VoiceOver meets every item in the same
// place with the same label. Decoration follows the density guardrails
// (density.ts) and the castle Full / Flat / Accessible modes; Reduce Motion
// stills the lean.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactElement } from 'react';
import { FlatList, Image, Pressable, ScrollView, View } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import type { ItemSummaryDto } from '@/api/generated/models';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { spacing } from '@/theme/tokens';
import { Cover, FormatBadge, Text } from '@/ui';
import { BannerMarker, useCastleArt, useCastleMode, type BannerMedium, type CastleMode } from '@/ui/castle';
import { maybeEntering, maybeLayout, motionMs, sharedCoverTag, staggerDelay } from '@/ui/motion';
import { castleDensityForCount } from './density';
import { bannerMediumForKind, formatLabels } from './labels';

export type Shelf = { key: string; index: number; items: ItemSummaryDto[] };

/** Consecutive runs of the grid order: shelf 1 holds the first run, and so on. */
export function toShelves(items: ItemSummaryDto[], shelfSize: number): Shelf[] {
  const size = Math.max(Math.floor(shelfSize), 1);
  const shelves: Shelf[] = [];
  for (let start = 0; start < items.length; start += size) {
    shelves.push({ key: `shelf-${shelves.length}`, index: shelves.length, items: items.slice(start, start + size) });
  }
  return shelves;
}

type CastleShelfListProps = {
  header: ReactElement;
  empty: ReactElement;
  items: ItemSummaryDto[];
  refreshing: boolean;
  onRefresh: () => void;
  hasNextPage: boolean;
  fetchNextPage: () => void;
  isFetchingNextPage: boolean;
  shelfSize: number;
  tileWidth: number;
  onOpenItem: (id: string) => void;
};

export function CastleShelfList({
  header,
  empty,
  items,
  refreshing,
  onRefresh,
  hasNextPage,
  fetchNextPage,
  isFetchingNextPage,
  shelfSize,
  tileWidth,
  onOpenItem,
}: CastleShelfListProps) {
  const reduceMotion = useReduceMotion();
  const density = castleDensityForCount(items.length);
  return (
    <FlatList<Shelf>
      testID="castle-shelf"
      data={toShelves(items, shelfSize)}
      keyExtractor={(shelf) => shelf.key}
      contentContainerStyle={styles.listContent}
      ListHeaderComponent={header}
      ListEmptyComponent={empty}
      refreshing={refreshing}
      onRefresh={onRefresh}
      onEndReachedThreshold={0.5}
      onEndReached={() => {
        if (hasNextPage && !isFetchingNextPage) fetchNextPage();
      }}
      ListFooterComponent={isFetchingNextPage ? <Text variant="caption" tone="muted">Loading more…</Text> : null}
      renderItem={({ item: shelf }) => (
        <ShelfRow shelf={shelf} framed={density !== 'covers'} tileWidth={tileWidth} reduceMotion={reduceMotion} onOpenItem={onOpenItem} />
      )}
    />
  );
}

/** The shelf's marker cloth follows its most common medium. */
function shelfMedium(items: ItemSummaryDto[]): BannerMedium | undefined {
  const counts = new Map<string, number>();
  for (const item of items) counts.set(item.kind, (counts.get(item.kind) ?? 0) + 1);
  let top: string | undefined;
  for (const [kind, count] of counts) {
    if (top === undefined || count > (counts.get(top) ?? 0)) top = kind;
  }
  return top === undefined ? undefined : bannerMediumForKind(top as ItemSummaryDto['kind']);
}

function ShelfRow({
  shelf,
  framed,
  tileWidth,
  reduceMotion,
  onOpenItem,
}: {
  shelf: Shelf;
  framed: boolean;
  tileWidth: number;
  reduceMotion: boolean;
  onOpenItem: (id: string) => void;
}) {
  const mode = useCastleMode();
  const art = useCastleArt();
  return (
    <Animated.View
      entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick).delay(staggerDelay(shelf.index)))}
      layout={maybeLayout(reduceMotion)}
      style={styles.shelf}
    >
      {framed ? <BannerMarker label={`Shelf ${shelf.index + 1}`} medium={shelfMedium(shelf.items)} testID={`shelf-${shelf.index + 1}-marker`} /> : null}
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        snapToInterval={tileWidth + spacing.md}
        decelerationRate="fast"
        contentContainerStyle={styles.row}
      >
        {shelf.items.map((item, position) => (
          <BookTile key={item.id} item={item} width={tileWidth} lean={leanFor(position, reduceMotion, mode)} onPress={() => onOpenItem(item.id)} />
        ))}
      </ScrollView>
      {mode === 'full' ? (
        <Image source={art.ledge} resizeMode="repeat" style={styles.ledge} testID="shelf-ledge" />
      ) : (
        <View style={mode === 'accessible' ? styles.dividerAccessible : styles.divider} testID="shelf-ledge" />
      )}
    </Animated.View>
  );
}

type Lean = '0deg' | '3deg' | '-3deg';

/** Neighboring books lean opposite ways, like a lived-in shelf; still under Reduce Motion and in Accessible mode. */
function leanFor(position: number, reduceMotion: boolean, mode: CastleMode): Lean {
  if (reduceMotion || mode === 'accessible') return '0deg';
  return position % 2 === 0 ? '3deg' : '-3deg';
}

const SPINE_WIDTH = 8;

function BookTile({ item, width, lean, onPress }: { item: ItemSummaryDto; width: number; lean: Lean; onPress: () => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  const reduceMotion = useReduceMotion();
  return (
    <Animated.View style={styles.book(lean, width)}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={item.editionVerified ? `${item.title}, ${details}` : `${item.title}, ${details}, edition unverified`}
        onPress={onPress}
        testID={`item-${item.id}`}
        style={styles.bookPressable}
      >
        <View style={styles.standing}>
          <View style={styles.spine(bannerMediumForKind(item.kind))} />
          <Cover title={item.title} uri={item.coverUrl} width={width - SPINE_WIDTH} sharedTag={sharedCoverTag(reduceMotion, item.id)} />
        </View>
        <Text variant="label" numberOfLines={2}>
          {item.title}
        </Text>
        <View style={styles.badges}>
          <FormatBadge label={formatLabels[item.format]} />
          {item.editionVerified ? null : <FormatBadge label="Edition unverified" />}
        </View>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  listContent: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  shelf: { gap: theme.spacing.sm },
  row: { gap: theme.spacing.md },
  ledge: { height: 16, width: '100%' },
  divider: { height: 1, backgroundColor: theme.colors.line },
  dividerAccessible: { height: 2, backgroundColor: theme.castle.iron },
  book: (lean: Lean, width: number) =>
    lean === '0deg' ? { width } : { width, transform: [{ perspective: 600 }, { rotateY: lean }] },
  bookPressable: { gap: theme.spacing.xs },
  standing: { flexDirection: 'row' },
  spine: (medium: BannerMedium | undefined) => ({
    width: SPINE_WIDTH,
    borderTopLeftRadius: theme.radius.sm,
    borderBottomLeftRadius: theme.radius.sm,
    backgroundColor:
      medium === 'book'
        ? theme.castle.bannerBook
        : medium === 'game'
          ? theme.castle.bannerGame
          : medium === 'film'
            ? theme.castle.bannerFilm
            : theme.castle.iron,
  }),
  badges: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.xs },
}));
