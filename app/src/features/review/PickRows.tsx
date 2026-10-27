// A pick-one row's editions (#440): each candidate is a tappable row — catalog art (or the
// captured crop, never a text tile), title, creators, year, and format or publisher — with
// radio behavior, and the secondary paths in one compact chip row underneath.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { ActivityIndicator, Pressable, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import type { ReleaseDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { isWeb } from '@/lib/auth/session';
import { Chip, Cover, Text, usePressFeedback } from '@/ui';
import { alternativeCoverOf, ownedCountOf, platformNameOf, type Preselection } from './grouping';

const COVER_WIDTH = 48;

/** One edition's spoken name: title, year, and the tail that tells editions apart. */
export function optionName(r: ReleaseDto): string {
  const tail = [platformNameOf(r) ?? formatLabels[r.format] ?? '', r.edition ?? '', r.publisher ?? ''].filter(Boolean).join(', ');
  return `${r.title}${r.year ? ` (${r.year})` : ''}${tail ? `, ${tail}` : ''}`;
}

/** The catalog facts under the title: creators, year, and format or publisher where provided. */
function optionFacts(r: ReleaseDto): string {
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

export function PickOptions({
  options,
  selectedId,
  suggested,
  cropUri,
  disabled,
  onSelect,
}: {
  options: readonly ReleaseDto[];
  selectedId: string | null;
  suggested: Preselection | null;
  /** The row's captured crop: the cover fallback when the catalog has no art. */
  cropUri: string | null;
  disabled: boolean;
  onSelect(option: ReleaseDto): void;
}) {
  if (options.length === 0) return null;
  return (
    <View style={styles.list}>
      {options.map((r) => (
        <OptionRow
          key={r.id}
          option={r}
          coverUri={alternativeCoverOf(r) ?? cropUri}
          selected={r.id === selectedId}
          reason={suggested?.releaseId === r.id ? suggested.reason : null}
          disabled={disabled}
          onSelect={() => onSelect(r)}
        />
      ))}
    </View>
  );
}

function OptionRow({
  option,
  coverUri,
  selected,
  reason,
  disabled,
  onSelect,
}: {
  option: ReleaseDto;
  coverUri: string | null;
  selected: boolean;
  reason: string | null;
  disabled: boolean;
  onSelect(): void;
}) {
  const press = usePressFeedback('select');
  const name = optionName(option);
  const facts = optionFacts(option);
  const owned = ownedCountOf(option);
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="radio"
        accessibilityLabel={`Select ${name}`}
        accessibilityState={{ selected, disabled }}
        // React Native Web ignores accessibilityState: the web reads the granular prop instead.
        aria-checked={isWeb() ? selected : undefined}
        disabled={disabled}
        onPress={() => {
          press.buzz();
          onSelect();
        }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={styles.row(selected)}
      >
        {coverUri ? (
          <Cover title={name} uri={coverUri} width={COVER_WIDTH} />
        ) : (
          // No art anywhere: a plain tile, never a text stand-in for the cover (#440).
          <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.tile} />
        )}
        <View style={styles.text}>
          <Text>{option.title}</Text>
          {facts ? (
            <Text variant="mono" tone="muted">
              {facts}
            </Text>
          ) : null}
          {reason ? <Text variant="caption" tone="accent">{reason}</Text> : null}
          {owned > 0 ? (
            <Text variant="caption" tone="muted">
              {`You own ${owned}`}
            </Text>
          ) : null}
        </View>
        <View style={styles.radio(selected)}>
          {selected ? <View style={styles.dot} /> : null}
        </View>
      </Pressable>
    </Animated.View>
  );
}

/** The pick-one row's secondary paths: details, keep, skip, reshoots, and discard as small chips, not full-width buttons. Discard sits last: it throws the row away. */
export function PickOverflow({
  title,
  canKeep,
  busy,
  onDetails,
  onKeep,
  onSkip,
  onScanBarcode,
  onSnapCover,
  onDiscard,
}: {
  title: string;
  canKeep: boolean;
  busy: boolean;
  onDetails(): void;
  onKeep(): void;
  onSkip?: () => void;
  onScanBarcode?: () => void;
  onSnapCover?: () => void;
  onDiscard?: () => void;
}) {
  const { theme } = useUnistyles();
  return (
    <View style={styles.overflow} accessibilityState={busy ? { busy: true } : undefined}>
      {busy ? <ActivityIndicator color={theme.colors.muted} /> : null}
      <Chip label="Details" accessibilityLabel={`Details for ${title}`} onPress={busy ? undefined : onDetails} />
      {canKeep ? <Chip label="Keep as unverified" accessibilityLabel={`Keep ${title} as unverified`} onPress={busy ? undefined : onKeep} /> : null}
      {onSkip ? <Chip label="Skip" accessibilityLabel={`Skip ${title}`} onPress={busy ? undefined : onSkip} /> : null}
      {onScanBarcode ? <Chip label="Scan barcode" accessibilityLabel={`Scan barcode for ${title}`} onPress={busy ? undefined : onScanBarcode} /> : null}
      {onSnapCover ? <Chip label="Snap cover" accessibilityLabel={`Snap cover for ${title}`} onPress={busy ? undefined : onSnapCover} /> : null}
      {onDiscard ? <Chip label="Discard" accessibilityLabel={`Discard ${title}`} onPress={busy ? undefined : onDiscard} /> : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  list: { gap: theme.spacing.sm },
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
  overflow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: theme.spacing.sm },
}));
