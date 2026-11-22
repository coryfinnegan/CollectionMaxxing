// The match on a walk card (#833): the cover, large, with its facts beside it. With other matches the cover is a paging reel: swipe
// to the next big cover and the facts change with it. A swipe only flips between matches; it never adds or skips.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { forwardRef, useEffect, useRef, useState, type ReactNode } from 'react';
import { ScrollView, View, type LayoutChangeEvent, type NativeScrollEvent, type NativeSyntheticEvent } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ReleaseDto } from '@/api/generated/models';
import { formatLabels, platformNameOf } from '@/features/library/labels';
import { Cover, Text } from '@/ui';

/** Cover width on the card; at the largest text sizes it drops and the facts go below it. */
export const COVER_WIDTH = 160;
export const COVER_WIDTH_LARGE_TEXT = 120;
/** From here on (iOS's larger accessibility sizes) the facts stack under the cover. */
export const LARGE_TEXT_SCALE = 2.3;

export type MatchOption = {
  release: ReleaseDto;
  /** Catalog art; absent means no cover art at all. */
  coverUri: string | null;
  /** "You own 1 · Shelf 2". */
  owned: string | null;
  /** "Cover match": the engine's reason for preselecting this one. */
  reason: string | null;
};

/** "Frank Herbert", "1990 · Paperback": the facts as the card shows and VoiceOver reads them. */
export function factsOf(r: ReleaseDto): { creators: string; detail: string } {
  const creators = Array.isArray(r.creators) ? r.creators.join(', ') : '';
  const what = platformNameOf(r) ?? formatLabels[r.format] ?? '';
  return { creators, detail: [r.year ? String(r.year) : '', what].filter(Boolean).join(' · ') };
}

export function matchLabel(option: MatchOption, index: number, count: number): string {
  const { creators, detail } = factsOf(option.release);
  const what = [option.release.title, creators, detail.replace(' · ', ', ')].filter(Boolean).join(', ');
  return count > 1 ? `Match ${index + 1} of ${count}: ${what}` : what;
}

export const MatchReel = forwardRef<View, {
  options: readonly MatchOption[];
  index: number;
  onIndex(index: number): void;
  /** What fills the cover slot when the match has no art (the phone's own picture of a cover), or nothing. */
  ownPicture?: ReactNode;
  /** No cover and no picture at all: the card is the words, large, with what the phone read and why there is no picture. */
  bare?: { readAs: string | null; note: string | null };
  fontScale: number;
}>(function MatchReel({ options, index, onIndex, ownPicture, bare, fontScale }, ref) {
  const [width, setWidth] = useState(0);
  const scroller = useRef<ScrollView>(null);
  const shown = useRef(index);
  const large = fontScale >= LARGE_TEXT_SCALE;
  const many = options.length > 1;
  const optionsKey = options.map((o) => o.release.id).join('|');

  // Opening on a match other than the first (the preselected one) scrolls the reel to it once the reel has a width.
  useEffect(() => {
    if (width <= 0) return;
    shown.current = index;
    scroller.current?.scrollTo({ x: index * width, animated: false });
    // Only a new width or a card change moves the reel; a swipe already put it there.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [width, optionsKey]);

  const settle = (e: NativeSyntheticEvent<NativeScrollEvent>) => {
    const page = e.nativeEvent.layoutMeasurement?.width || width;
    if (page <= 0) return;
    const next = Math.max(0, Math.min(options.length - 1, Math.round(e.nativeEvent.contentOffset.x / page)));
    if (next === shown.current) return;
    shown.current = next;
    onIndex(next);
  };
  const step = (by: number) => {
    const next = Math.max(0, Math.min(options.length - 1, index + by));
    if (next === index) return;
    shown.current = next;
    scroller.current?.scrollTo({ x: next * width, animated: true });
    onIndex(next);
  };
  const current = options[index] ?? options[0];
  if (!current) return null;

  return (
    <View
      ref={ref}
      testID="match-reel"
      accessible
      accessibilityRole={many ? 'adjustable' : 'text'}
      accessibilityLabel={matchLabel(current, index, options.length)}
      accessibilityActions={many ? [{ name: 'increment' }, { name: 'decrement' }] : undefined}
      onAccessibilityAction={(e) => step(e.nativeEvent.actionName === 'increment' ? 1 : -1)}
      onLayout={(e: LayoutChangeEvent) => setWidth(e.nativeEvent.layout.width)}
    >
      <ScrollView
        ref={scroller}
        testID="match-pages"
        horizontal
        pagingEnabled
        scrollEnabled={many}
        showsHorizontalScrollIndicator={false}
        scrollEventThrottle={16}
        onScroll={settle}
        onMomentumScrollEnd={settle}
      >
        {options.map((option, i) => (
          <Page key={option.release.id} option={option} index={i} count={options.length} width={width} large={large} ownPicture={ownPicture} bare={bare} />
        ))}
      </ScrollView>
    </View>
  );
});

function Page({
  option,
  index,
  count,
  width,
  large,
  ownPicture,
  bare,
}: {
  option: MatchOption;
  index: number;
  count: number;
  width: number;
  large: boolean;
  ownPicture?: ReactNode;
  bare?: { readAs: string | null; note: string | null };
}) {
  const { creators, detail } = factsOf(option.release);
  const coverWidth = large ? COVER_WIDTH_LARGE_TEXT : COVER_WIDTH;
  const slot = option.coverUri ? <Cover title={option.release.title} uri={option.coverUri} width={coverWidth} /> : ownPicture;
  return (
    <View testID={bare ? 'walk-bare' : undefined} style={[styles.page(width, large), bare ? styles.bare : null]}>
      {slot ? <View testID={option.coverUri ? 'match-cover' : 'match-own-picture'}>{slot}</View> : null}
      <View style={styles.facts(large)}>
        <Text variant={bare ? 'title' : 'heading'} numberOfLines={4}>
          {option.release.title}
        </Text>
        {creators ? <Text numberOfLines={2}>{creators}</Text> : null}
        {detail ? <Text variant="mono">{detail}</Text> : null}
        {option.owned ? <Text tone="muted">{option.owned}</Text> : null}
        {option.reason ? (
          <Text variant="caption" tone="accent">
            {option.reason}
          </Text>
        ) : null}
        {bare?.readAs ? <Text variant="mono">{bare.readAs}</Text> : null}
        {bare?.note ? <Text tone="muted">{bare.note}</Text> : null}
        {count > 1 ? (
          <Text variant="caption" tone="muted">
            {`Match ${index + 1} of ${count}`}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  page: (width: number, large: boolean) => ({
    width: width > 0 ? width : undefined,
    flexDirection: large ? ('column' as const) : ('row' as const),
    alignItems: 'flex-start' as const,
    gap: theme.spacing.md,
  }),
  bare: {
    padding: theme.spacing.xl,
    borderRadius: theme.radius.lg,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
  facts: (large: boolean) => (large ? { alignSelf: 'stretch' as const, gap: theme.spacing.xs } : { flex: 1, minWidth: 0, gap: theme.spacing.xs }),
}));
