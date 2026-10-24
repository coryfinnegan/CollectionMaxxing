// Review (docs/06 M5): everything scanned in one place, grouped by how much attention it needs, added in one tap.
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Pressable, View } from 'react-native';
import type { CandidateDto, LocationDto } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { listPlatforms } from '@/api/generated/catalog/catalog';
import { conditionLabels, conditions, formatLabels, formats } from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { useLocationsList } from '@/features/locations/useLocations';
import { NOT_FOUND_COPY, QUOTA_COPY } from '@/features/scan/cards/copy';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Chip, EmptyState, ListRow, LoadingState, Screen, Sheet, Snackbar, Text, TextField } from '@/ui';
import { alternativesOf, isCollapsed, type ReviewGroup } from './grouping';
import { addLabel, effective, type ReviewStatus } from './plan';
import { useReview } from './useReview';

type Review = ReturnType<typeof useReview>;

const TITLES: Record<ReviewGroup, string> = {
  pick: 'Pick one',
  notFound: 'Not found',
  owned: 'You own these',
  ready: 'Ready',
  pending: 'Still looking up',
};
const ORDER: readonly ReviewGroup[] = ['pick', 'notFound', 'owned', 'ready', 'pending'];
const STATUS_LABEL: Record<ReviewStatus, string> = { owned: 'Owned', wishlist: 'Wishlist' };

const titleOf = (c: CandidateDto) => c.release?.title ?? (c.visibleText ? `"${c.visibleText}"` : c.rawCode ? `Code ${c.rawCode}` : 'Unknown item');
const subtitleOf = (c: CandidateDto) => {
  const r = c.release;
  if (!r) return '';
  const what = r.platform?.name ?? formatLabels[r.format] ?? '';
  return [what, r.year].filter(Boolean).join(' · ');
};

export function ReviewScreen({ libraryId, sessionId, onBack, undoMs }: { libraryId: string; sessionId: string | null; onBack?: () => void; undoMs?: number }) {
  const review = useReview({ libraryId, sessionId, undoMs });
  const { locations } = useLocationsList(libraryId);
  const [pickingFor, setPickingFor] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const total = review.list.length;
  const addTo = breadcrumb(locations, review.draft.addTo);

  return (
    <Screen title="Review" subtitle={review.canAdd ? undefined : 'You can look but not add. Ask an owner or editor to add these.'}>
      {total > 0 ? <Text tone="muted">{`${total} ${total === 1 ? 'item' : 'items'} scanned`}</Text> : null}
      {onBack ? <Button label="Back" variant="secondary" onPress={onBack} /> : null}
      {review.loading ? <LoadingState label="Loading what you scanned…" /> : null}
      {review.loadError ? (
        <Text tone="danger" accessibilityRole="alert">
          {review.loadError}
        </Text>
      ) : null}
      {!review.loading && total === 0 ? <EmptyState title="Nothing to review." body="Scan something and it shows up here." /> : null}

      {review.canAdd && total > 0 ? (
        <View style={{ gap: spacing.sm }}>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
            <Text variant="label">Add to</Text>
            <Button label={addTo} accessibilityLabel={`Add to: ${addTo}. Change`} variant="secondary" onPress={() => setPickingFor(pickingFor === 'all' ? null : 'all')} />
          </View>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.sm }}>
            <Text variant="label">Status</Text>
            {(['owned', 'wishlist'] as const).map((s) => (
              <Chip key={s} label={STATUS_LABEL[s]} accessibilityLabel={`Status ${STATUS_LABEL[s]}`} selected={review.draft.status === s} onPress={() => review.setStatus(s)} />
            ))}
          </View>
          {pickingFor === 'all' ? (
            <LocationSheet
              locations={locations}
              onPick={(id) => {
                review.setAddTo(id);
                setPickingFor(null);
              }}
              onClose={() => setPickingFor(null)}
            />
          ) : null}
        </View>
      ) : null}

      {ORDER.map((group) => {
        const rows = review.groups[group];
        if (rows.length === 0) return null;
        const collapsed = group === 'ready' && isCollapsed(rows.length) && !expanded;
        return (
          <View key={group} style={{ gap: spacing.sm }}>
            <Text variant="heading" accessibilityRole="header">{`${TITLES[group]} · ${rows.length}`}</Text>
            {collapsed ? (
              <Button label={`${rows.length} items ready. Show them`} variant="secondary" onPress={() => setExpanded(true)} />
            ) : (
              rows.map((c) => (
                <Row
                  key={c.id}
                  c={c}
                  group={group}
                  review={review}
                  isOpen={open === c.id}
                  onOpen={() => setOpen(open === c.id ? null : c.id)}
                  locations={locations}
                  pickingFor={pickingFor}
                  setPickingFor={setPickingFor}
                />
              ))
            )}
          </View>
        );
      })}

      {review.commitError ? (
        <Text tone="danger" accessibilityRole="alert">
          {review.commitError}
        </Text>
      ) : null}
      {review.canAdd && total > 0 ? (
        <Button label={addLabel(review.count)} busy={review.committing} disabled={review.count === 0} onPress={() => void review.commit()} />
      ) : null}
      {review.toast ? (
        <Snackbar message={review.toast.message} actionLabel={review.toast.canUndo ? 'Undo' : undefined} onAction={() => void review.undo()} />
      ) : null}
    </Screen>
  );
}

function LocationSheet({ locations, onPick, onClose }: { locations: LocationDto[]; onPick(id: string | null): void; onClose(): void }) {
  return (
    <Sheet title="Pick a location" onClose={onClose}>
      <ListRow title="Not placed" onPress={() => onPick(null)} />
      {flattenTree(locations).map(({ location, depth }) => (
        <ListRow key={location.id} title={location.name} depth={depth} onPress={() => onPick(location.id)} />
      ))}
    </Sheet>
  );
}

type RowProps = {
  c: CandidateDto;
  group: ReviewGroup;
  review: Review;
  isOpen: boolean;
  onOpen(): void;
  locations: LocationDto[];
  pickingFor: string | null;
  setPickingFor(id: string | null): void;
};

function Row({ c, group, review, isOpen, onOpen, locations, pickingFor, setPickingFor }: RowProps) {
  const colors = useColors();
  const [text, setText] = useState('');
  const checkable = review.canCheck(c);
  const checked = checkable && review.isChecked(c);
  const title = titleOf(c);
  const error = review.rowErrors[c.id];
  const busy = !!review.rowBusy[c.id];
  const alternatives = group === 'pick' ? alternativesOf(c) : [];
  const detail = [
    subtitleOf(c),
    c.copies > 1 ? `${c.copies} copies` : '',
    c.alreadyOwnedCount > 0 ? `You own ${c.alreadyOwnedCount}${c.ownedAt[0] ? ` · ${c.ownedAt[0]}` : ''}` : '',
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <View style={{ gap: spacing.sm, padding: spacing.md, borderRadius: radius.md, borderWidth: 1, borderColor: colors.line, backgroundColor: colors.surface }}>
      <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
        {checkable ? (
          <Pressable
            accessibilityRole="checkbox"
            accessibilityLabel={`Add ${title}`}
            accessibilityState={{ checked }}
            onPress={() => review.toggle(c)}
            style={{ minWidth: 44, minHeight: 44, alignItems: 'center', justifyContent: 'center' }}
          >
            <Text>{checked ? '☑' : '☐'}</Text>
          </Pressable>
        ) : null}
        <Pressable accessibilityRole="button" accessibilityLabel={`Details for ${title}`} onPress={onOpen} style={{ flex: 1, minHeight: 44, justifyContent: 'center' }}>
          <Text>{title}</Text>
          {detail ? (
            <Text variant="caption" tone="muted">
              {detail}
            </Text>
          ) : null}
        </Pressable>
      </View>

      {alternatives.map((r) => (
        <Button
          key={r.id}
          label={`${r.title}${r.year ? ` (${r.year})` : ''} ${formatLabels[r.format] ?? ''}`.trim()}
          variant="secondary"
          disabled={busy || !review.canAdd}
          onPress={() => void review.choose(c, r)}
        />
      ))}
      {group === 'pick' && review.canAdd ? <Button label={`Skip ${title}`} variant="secondary" disabled={busy} onPress={() => void review.skip(c)} /> : null}

      {group === 'notFound' ? (
        <>
          <Text tone="muted">{NOT_FOUND_COPY}</Text>
          {review.canAdd ? (
            <>
              <TextField label="Title" value={text} onChangeText={setText} placeholder="Space Mutiny" />
              <View style={{ flexDirection: 'row', gap: spacing.sm }}>
                <Button label="Search" busy={busy} disabled={!text.trim()} onPress={() => void review.search(c, text.trim())} />
                <Button label="Snap cover" accessibilityLabel="Snap cover, coming soon" variant="secondary" disabled />
              </View>
              <Text variant="caption" tone="muted">
                Snap cover is coming soon.
              </Text>
            </>
          ) : null}
        </>
      ) : null}

      {group === 'pending' && c.pendingReason === 'quota' ? <Text tone="muted">{QUOTA_COPY}</Text> : null}

      {group === 'owned' && review.canAdd ? (
        <View style={{ flexDirection: 'row', gap: spacing.sm }}>
          <Button label={`Skip ${title}`} variant="secondary" disabled={busy} onPress={() => void review.skip(c)} />
          <Button label={`Add another copy of ${title}`} variant="secondary" disabled={busy} onPress={() => review.toggle(c)} />
        </View>
      ) : null}

      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}

      {isOpen ? <CandidateSheet c={c} review={review} onClose={onOpen} locations={locations} pickingFor={pickingFor} setPickingFor={setPickingFor} /> : null}
    </View>
  );
}

type SheetProps = Pick<RowProps, 'c' | 'review' | 'locations' | 'pickingFor' | 'setPickingFor'> & { onClose(): void };

function CandidateSheet({ c, review, onClose, locations, pickingFor, setPickingFor }: SheetProps) {
  const title = titleOf(c);
  const line = effective(review.draft, c.id);
  const place = breadcrumb(locations, line.locationId);
  return (
    <Sheet title={title} onClose={onClose}>
      <Text tone="muted">{subtitleOf(c) || 'No match yet'}</Text>
      {review.canAdd ? (
        <>
          {c.release ? <EditionFields c={c} title={title} review={review} /> : null}
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
            <Text variant="label">Copies</Text>
            <Button label="Fewer copies" variant="secondary" disabled={c.copies <= 1} onPress={() => void review.setCopies(c, c.copies - 1)} />
            <Text>{String(c.copies)}</Text>
            <Button label="More copies" variant="secondary" onPress={() => void review.setCopies(c, c.copies + 1)} />
          </View>
          <Text variant="label">Condition</Text>
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
            <Chip label="Not set" accessibilityLabel="Condition not set" selected={line.condition === null} onPress={() => review.override(c.id, { condition: null })} />
            {conditions.map((k) => (
              <Chip key={k} label={conditionLabels[k]} accessibilityLabel={`Condition ${conditionLabels[k]}`} selected={line.condition === k} onPress={() => review.override(c.id, { condition: k })} />
            ))}
          </View>
          <Text variant="label">Status</Text>
          <View style={{ flexDirection: 'row', gap: spacing.sm }}>
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
    </Sheet>
  );
}

/** Format, platform and edition of this scanned item, changed before it is added (a game on the wrong platform, a special edition). */
function EditionFields({ c, title, review }: { c: CandidateDto; title: string; review: Review }) {
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
    <View style={{ gap: spacing.sm }}>
      <Text variant="label">Format</Text>
      <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
        {formats.map((f) => (
          <Chip key={f} label={formatLabels[f]} accessibilityLabel={`Format for ${title}: ${formatLabels[f]}`} selected={currentFormat === f} onPress={() => void review.edit(c, { format: f })} />
        ))}
      </View>
      {(platforms.data ?? []).length > 0 ? (
        <>
          <Text variant="label">Platform</Text>
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
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
