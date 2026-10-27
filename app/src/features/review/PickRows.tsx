// A pick-one row's editions (#440): each candidate is a shared edition row
// (catalog art or the captured crop, title, creators, year, format or
// publisher) with radio behavior, and the secondary paths in one compact chip
// row underneath.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { ActivityIndicator, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import type { ReleaseDto } from '@/api/generated/models';
import { EditionRow } from '@/features/search/rows';
import { Chip } from '@/ui';
import { alternativeCoverOf, ownedCountOf, type Preselection } from './grouping';

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
        <EditionRow
          key={r.id}
          release={r}
          coverUri={alternativeCoverOf(r) ?? cropUri}
          reason={suggested?.releaseId === r.id ? suggested.reason : null}
          ownedCount={ownedCountOf(r)}
          selectable
          selected={r.id === selectedId}
          disabled={disabled}
          onPress={() => onSelect(r)}
        />
      ))}
    </View>
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
  overflow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: theme.spacing.sm },
}));
