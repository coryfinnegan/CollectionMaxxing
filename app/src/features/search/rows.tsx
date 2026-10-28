// The shared work-grouped result rows (#457): the work row (representative
// cover, title, creator, year span, edition count, owned badge) and the
// edition row (thumbnail, title, catalog facts). Review's pick-one options
// (#440) render through EditionRow, so the edition-row shape is identical in
// both places. Owned state arrives as props from local data until #456 lands
// its grouped provider data and owned ids in the response.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Pressable, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import type { CatalogSearchResultDto, ReleaseDto } from '@/api/generated/models';
import { formatLabels, platformNameOf } from '@/features/library/labels';
import { isWeb } from '@/lib/auth/session';
import { Cover, Text, usePressFeedback } from '@/ui';

const COVER_WIDTH = 48;

/** One edition's spoken name: title, year, and the tail that tells editions apart. */
export function editionName(r: ReleaseDto): string {
  const tail = [platformNameOf(r) ?? formatLabels[r.format] ?? '', r.edition ?? '', r.publisher ?? ''].filter(Boolean).join(', ');
  return `${r.title}${r.year ? ` (${r.year})` : ''}${tail ? `, ${tail}` : ''}`;
}

/** The catalog facts under the title: creators, year, and format or publisher where provided. */
export function editionFacts(r: ReleaseDto): string {
  const creators = Array.isArray(r.creators) ? r.creators : [];
  return [
    creators.length > 0 ? creators.join(', ') : '',
    r.year ? String(r.year) : '',
    platformNameOf(r) ?? formatLabels[r.format] ?? '',
    r.edition ?? '',
    r.publisher ?? '',
  ]
    .filter(Boolean)
    .join(' · ');
}

/** The year span across a work's editions: one year when they all share it, empty when none names one. */
export function yearSpanOf(releases: readonly ReleaseDto[]): string {
  const years = releases.map((r) => r.year).filter((y): y is number => typeof y === 'number');
  if (years.length === 0) return '';
  const [first, last] = [Math.min(...years), Math.max(...years)];
  return first === last ? String(first) : `${first}–${last}`;
}

/**
 * The work row's cover: an owned edition's cover wins, then the first-scored
 * edition with art, then the work's own art. Nothing anywhere means the plain
 * tile, never a text stand-in for the cover (#440).
 */
export function representativeCoverOf(result: CatalogSearchResultDto, ownedReleaseIds: ReadonlySet<string>): string | null {
  return (
    result.releases.find((r) => ownedReleaseIds.has(r.id) && r.coverUrl)?.coverUrl ??
    result.releases.find((r) => r.coverUrl)?.coverUrl ??
    result.work.coverUrl
  );
}

export function EditionRow({
  release,
  coverUri,
  reason,
  ownedCount = 0,
  selectable = false,
  selected = false,
  disabled = false,
  onPress,
  testID,
}: {
  release: ReleaseDto;
  /** The cover address, defaulting to the catalog's (review passes its captured crop as the fallback). */
  coverUri?: string | null;
  /** The matching engine's short reason, shown beside a review row. */
  reason?: string | null;
  ownedCount?: number;
  /** Review's pick-one rows: radio behavior with the selection ring; search rows are plain buttons. */
  selectable?: boolean;
  selected?: boolean;
  disabled?: boolean;
  onPress(): void;
  testID?: string;
}) {
  const press = usePressFeedback(selectable ? 'select' : 'tap');
  const name = editionName(release);
  const facts = editionFacts(release);
  const uri = coverUri ?? release.coverUrl;
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole={selectable ? 'radio' : 'button'}
        accessibilityLabel={selectable ? `Select ${name}` : name}
        accessibilityState={selectable ? { selected, disabled } : disabled ? { disabled } : undefined}
        // React Native Web ignores accessibilityState: the web reads the granular prop instead.
        aria-checked={selectable && isWeb() ? selected : undefined}
        disabled={disabled}
        onPress={() => {
          press.buzz();
          onPress();
        }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={styles.row(selectable && selected)}
        testID={testID}
      >
        {uri ? (
          <Cover title={name} uri={uri} width={COVER_WIDTH} />
        ) : (
          // No art anywhere: a plain tile, never a text stand-in for the cover (#440).
          <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.tile} />
        )}
        <View style={styles.text}>
          <Text>{release.title}</Text>
          {facts ? (
            <Text variant="mono" tone="muted">
              {facts}
            </Text>
          ) : null}
          {reason ? <Text variant="caption" tone="accent">{reason}</Text> : null}
          {ownedCount > 0 ? (
            <Text variant="caption" tone="muted">
              {`You own ${ownedCount}`}
            </Text>
          ) : null}
        </View>
        {selectable ? (
          <View style={styles.radio(selected)}>
            {selected ? <View style={styles.dot} /> : null}
          </View>
        ) : null}
      </Pressable>
    </Animated.View>
  );
}

export function WorkRow({
  result,
  ownedReleaseIds,
  onPress,
  testID,
}: {
  result: CatalogSearchResultDto;
  /** The owned edition ids, from local data until #456 exposes them in the response. */
  ownedReleaseIds: ReadonlySet<string>;
  onPress(): void;
  testID?: string;
}) {
  const press = usePressFeedback('tap');
  const { work, releases } = result;
  const creators = work.creators.length > 0 ? work.creators : (releases[0]?.creators ?? []);
  const span = yearSpanOf(releases) || (work.originalYear ? String(work.originalYear) : '');
  const facts = [creators.length > 0 ? creators.join(', ') : '', span, `${releases.length} ${releases.length === 1 ? 'edition' : 'editions'}`]
    .filter(Boolean)
    .join(' · ');
  const owned = releases.some((r) => ownedReleaseIds.has(r.id));
  const uri = representativeCoverOf(result, ownedReleaseIds);
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${work.title}, ${facts}${owned ? ', in your library' : ''}`}
        onPress={() => {
          press.buzz();
          onPress();
        }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={styles.row(false)}
        testID={testID}
      >
        {uri ? (
          <Cover title={work.title} uri={uri} width={COVER_WIDTH} />
        ) : (
          // No art anywhere: a plain tile, never a text stand-in for the cover (#440).
          <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.tile} />
        )}
        <View style={styles.text}>
          <Text>{work.title}</Text>
          {facts ? (
            <Text variant="mono" tone="muted">
              {facts}
            </Text>
          ) : null}
          {owned ? (
            <View style={styles.badge}>
              <Text variant="label" style={styles.badgeLabel}>
                In your library
              </Text>
            </View>
          ) : null}
        </View>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: (selected: boolean) => ({
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.md,
    paddingVertical: theme.spacing.sm,
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.md,
    borderWidth: 1,
    borderColor: selected ? theme.colors.accent : theme.colors.line,
    backgroundColor: theme.colors.surface,
  }),
  tile: {
    width: COVER_WIDTH,
    height: COVER_WIDTH * 1.5,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
  },
  text: { flex: 1, gap: theme.spacing.xs },
  badge: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.spacing.sm,
    paddingVertical: 2,
    borderRadius: theme.radius.sm,
    borderWidth: 1,
    borderColor: theme.colors.sticker,
    backgroundColor: theme.colors.surface,
  },
  badgeLabel: { color: theme.colors.stickerText },
  radio: (selected: boolean) => ({
    width: 22,
    height: 22,
    borderRadius: 11,
    borderWidth: 2,
    borderColor: selected ? theme.colors.accent : theme.colors.control,
    alignItems: 'center',
    justifyContent: 'center',
  }),
  dot: { width: 10, height: 10, borderRadius: 5, backgroundColor: theme.colors.accent },
}));
