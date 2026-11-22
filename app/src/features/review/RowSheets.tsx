// A Review row's sheets: where it goes, and its details before it is added (edition, copies, condition, status, location).
// Shared by the list and the walk's Fix it (#833).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto, LocationDto } from '@/api/generated/models';
import { listPlatforms } from '@/api/generated/catalog/catalog';
import { breadcrumb } from '@/features/item/breadcrumb';
import { conditionLabels, conditions, formatLabels, formats } from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { Button, Chip, ListRow, Sheet, Text, TextField } from '@/ui';
import { UNKNOWN_ITEM, unresolvedTitleOf } from './grouping';
import { effective, type ReviewStatus } from './plan';
import type { useReview } from './useReview';

export type Review = ReturnType<typeof useReview>;

export const STATUS_LABEL: Record<ReviewStatus, string> = { owned: 'Owned', wishlist: 'Wishlist' };

export const titleOf = (c: CandidateDto) => c.release?.title ?? unresolvedTitleOf(c) ?? (c.rawCode ? `Code ${c.rawCode}` : UNKNOWN_ITEM);

export const subtitleOf = (c: CandidateDto) => {
  const r = c.release;
  if (!r) return '';
  const what = r.platform?.name ?? formatLabels[r.format] ?? '';
  return [what, r.year].filter(Boolean).join(' · ');
};

export function LocationSheet({ locations, onPick, onClose }: { locations: LocationDto[]; onPick(id: string | null): void; onClose(): void }) {
  return (
    <Sheet title="Pick a location" onClose={onClose}>
      <ListRow title="Not placed" onPress={() => onPick(null)} />
      {flattenTree(locations).map(({ location, depth }) => (
        <ListRow key={location.id} title={location.name} depth={depth} onPress={() => onPick(location.id)} />
      ))}
    </Sheet>
  );
}

export type SheetProps = {
  c: CandidateDto;
  review: Review;
  locations: LocationDto[];
  pickingFor: string | null;
  setPickingFor(id: string | null): void;
  onClose(): void;
};

export function CandidateSheet({ c, review, onClose, locations, pickingFor, setPickingFor }: SheetProps) {
  return (
    <Sheet title={titleOf(c)} onClose={onClose}>
      <CandidateFields c={c} review={review} locations={locations} pickingFor={pickingFor} setPickingFor={setPickingFor} />
    </Sheet>
  );
}

/** The sheet's body: the row's edition, copies, condition, status and location. */
export function CandidateFields({ c, review, locations, pickingFor, setPickingFor }: Omit<SheetProps, 'onClose'>) {
  const title = titleOf(c);
  const line = effective(review.draft, c.id);
  const place = breadcrumb(locations, line.locationId);
  return (
    <>
      <Text tone="muted">{subtitleOf(c) || 'No match yet'}</Text>
      {review.canAdd ? (
        <>
          {c.release ? <EditionFields c={c} title={title} review={review} /> : null}
          <View style={styles.line}>
            <Text variant="label">Copies</Text>
            <Button label="Fewer copies" variant="secondary" disabled={c.copies <= 1} onPress={() => void review.setCopies(c, c.copies - 1)} />
            <Text>{String(c.copies)}</Text>
            <Button label="More copies" variant="secondary" onPress={() => void review.setCopies(c, c.copies + 1)} />
          </View>
          <Text variant="label">Condition</Text>
          <View style={styles.chips}>
            <Chip label="Not set" accessibilityLabel="Condition not set" selected={line.condition === null} onPress={() => review.override(c.id, { condition: null })} />
            {conditions.map((k) => (
              <Chip key={k} label={conditionLabels[k]} accessibilityLabel={`Condition ${conditionLabels[k]}`} selected={line.condition === k} onPress={() => review.override(c.id, { condition: k })} />
            ))}
          </View>
          <Text variant="label">Status</Text>
          <View style={styles.row}>
            {(['owned', 'wishlist'] as const).map((s) => (
              <Chip key={s} label={STATUS_LABEL[s]} accessibilityLabel={`Status for ${title}: ${STATUS_LABEL[s]}`} selected={line.status === s} onPress={() => review.override(c.id, { status: s })} />
            ))}
          </View>
          <Button label={`Location for ${title}: ${place}`} variant="secondary" onPress={() => setPickingFor(pickingFor === c.id ? null : c.id)} />
          {pickingFor === c.id ? (
            <LocationSheet
              locations={locations}
              onPick={(id) => {
                review.override(c.id, { locationId: id });
                setPickingFor(null);
              }}
              onClose={() => setPickingFor(null)}
            />
          ) : null}
        </>
      ) : (
        <Text tone="muted">{c.alreadyOwnedCount > 0 ? `You own ${c.alreadyOwnedCount}.` : 'You do not own this yet.'}</Text>
      )}
    </>
  );
}

/** Format, platform and edition of this scanned item, changed before it is added (a game on the wrong platform, a special edition). */
export function EditionFields({ c, title, review }: { c: CandidateDto; title: string; review: Review }) {
  const platforms = useQuery({
    queryKey: ['catalog-platforms'],
    queryFn: async () => {
      const response = await listPlatforms();
      return response.status === 200 ? response.data.items : [];
    },
  });
  const currentFormat = c.edits?.format ?? c.release?.format;
  const currentPlatform = c.edits?.platform ?? c.release?.platform?.name ?? null;
  const [edition, setEdition] = useState(c.edits?.edition ?? c.release?.edition ?? '');
  const saved = c.edits?.edition ?? c.release?.edition ?? '';
  return (
    <View style={styles.stack}>
      <Text variant="label">Format</Text>
      <View style={styles.chips}>
        {formats.map((f) => (
          <Chip key={f} label={formatLabels[f]} accessibilityLabel={`Format for ${title}: ${formatLabels[f]}`} selected={currentFormat === f} onPress={() => void review.edit(c, { format: f })} />
        ))}
      </View>
      {(platforms.data ?? []).length > 0 ? (
        <>
          <Text variant="label">Platform</Text>
          <View style={styles.chips}>
            {(platforms.data ?? []).map((p) => (
              <Chip key={p.id} label={p.name} accessibilityLabel={`Platform for ${title}: ${p.name}`} selected={currentPlatform === p.name} onPress={() => void review.edit(c, { platform: p.name })} />
            ))}
          </View>
        </>
      ) : null}
      <TextField label="Edition" value={edition} onChangeText={setEdition} placeholder="Collector's edition" />
      <Button
        label="Save edition"
        accessibilityLabel={`Save edition for ${title}`}
        variant="secondary"
        disabled={edition.trim() === saved}
        onPress={() => void review.edit(c, { edition: edition.trim() })}
      />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  stack: { gap: theme.spacing.sm },
  line: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  row: { flexDirection: 'row', gap: theme.spacing.sm },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
}));
