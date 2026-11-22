// Review one at a time (#833), on a narrow screen: only the rows that need the person, each lit on its own shelf photo with a large
// cover, answered with a button, then the next. The confident rows wait at the end with the one Add ("Check them" walks them too).
import { useEffect, useMemo, useState } from 'react';
import { useWindowDimensions } from 'react-native';
import { useStore } from 'zustand';
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { useLocationsList } from '@/features/locations/useLocations';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { getPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { getSnapStore } from '@/features/scan/pipeline/snapStore';
import { EmptyState, LoadingState, Screen, Snackbar, Text } from '@/ui';
import { CommitCelebration } from './CommitCelebration';
import { alternativeCoverOf, alternativesOf, groupOf, ownedCountOf, preselectedOf } from './grouping';
import type { MatchOption } from './MatchReel';
import { placeOf, type Place } from './placement';
import { pairCropUri, thumbForLocalId } from './provisionals';
import type { ReviewProps } from './ReviewList';
import type { Review } from './RowSheets';
import { TextAction } from './TextAction';
import {
  aheadOf,
  answerWalk,
  confident,
  currentOf,
  emptyWalk,
  joinWalk,
  orderRows,
  progressLabel,
  progressOf,
  shelfPosition,
  stillReadingLine,
  undoWalk,
  walkQueue,
  type Answer,
  type WalkState,
} from './walk';
import { WalkCard, type CardModel, type WalkKind } from './WalkCard';
import { WalkEnd } from './WalkEnd';

type Checking = { kind: Exclude<WalkKind, 'needs'>; walk: WalkState };

export function ReviewWalk({ review, libraryId, onOpenResult, onScanBarcode, onSnapCover, onSeeAll }: ReviewProps & { review: Review; onSeeAll(): void }) {
  const { locations } = useLocationsList(libraryId);
  const { width, fontScale } = useWindowDimensions();
  const byId = useCardsStore((s) => s.byId);
  const cardsSkipped = useCardsStore((s) => s.skipped);
  const snaps = useStore(getPipelineStore(), (s) => s.snaps);
  // The snap store is read alongside the receipts: a photo's still and boxes are saved as its receipt moves on.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const stored = useMemo(() => [...getSnapStore().list()], [snaps]);
  const [checking, setChecking] = useState<Checking | null>(null);
  const [direction, setDirection] = useState<'forward' | 'back'>('forward');

  const places = useMemo(() => {
    const out: Record<string, Place | null> = {};
    for (const c of [...review.list, ...Object.values(cardsSkipped)]) out[c.id] = placeOf(c, { snaps, stored });
    return out;
  }, [review.list, cardsSkipped, snaps, stored]);
  const find = (id: string): CandidateDto | undefined => byId[id] ?? cardsSkipped[id] ?? review.skipped.find((r) => r.candidate.id === id)?.candidate;
  const exists = (id: string) => !!byId[id] || !!cardsSkipped[id];

  // The rows that need the person join the saved walk at its end; the order already walked never changes.
  const queue = walkQueue(review.list, review.draft, places).map((c) => c.id);
  const queueKey = queue.join('|');
  const needs = new Set(queue);
  const walk = joinWalk(review.walk, queue);
  const { updateWalk } = review;
  useEffect(() => {
    if (queue.length > 0) updateWalk((w) => joinWalk(w, queue));
    // The key stands for the queue.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queueKey, updateWalk]);
  const current = currentOf(walk, (id) => needs.has(id), exists);
  // The card on screen is the walk's place: it stays on screen if the server skips it or Fix it rematches it, and the walk comes
  // back to it after a trip to the camera or a restart.
  useEffect(() => {
    if (current && current !== walk.place) updateWalk((w) => ({ ...w, place: current }));
  }, [current, walk.place, updateWalk]);
  const reachedEnd = !current && !review.loading;
  useEffect(() => {
    if (reachedEnd && !walk.ended && review.sessionId) updateWalk((w) => ({ ...w, ended: true }));
  }, [reachedEnd, walk.ended, review.sessionId, updateWalk]);
  const atEnd = walk.ended || reachedEnd;

  // "Check them": the same card walk over the confident rows, or the rows you own. It lives only while it runs.
  const checkIds = useMemo(() => {
    if (!checking) return [];
    const rows = checking.kind === 'owned' ? review.groups.owned : review.list.filter((c) => confident(c, review.draft));
    return orderRows(rows, places).map((c) => c.id);
  }, [checking, review.groups.owned, review.list, review.draft, places]);
  const checkSet = new Set(checkIds);
  const checkWalk = checking ? joinWalk(checking.walk, checkIds) : null;
  // A check walk with nothing left is over: the end shows again.
  const checkCurrent = checkWalk ? currentOf(checkWalk, (id) => checkSet.has(id), exists) : null;

  // Photos the server is still reading for this scan: their finds join the end of the walk.
  const reading = stillReadingLine(
    snaps.filter((s) => s.libraryId === libraryId && (!s.sent || s.sessionId === review.sessionId) && (s.phase === 'reading' || !s.sent || !!s.shelfUpload)).length,
  );

  const photos = useMemo(() => {
    const seen = new Map<string, number>();
    for (const id of walk.order) {
      const place = places[id];
      if (place?.still && !seen.has(place.snapId)) seen.set(place.snapId, place.takenAt);
    }
    return [...seen.entries()].sort((a, b) => a[1] - b[1]).map(([snapId]) => snapId);
  }, [walk.order, places]);

  const modelOf = (c: CandidateDto, kind: WalkKind): CardModel => {
    const place = places[c.id] ?? null;
    const pre = preselectedOf(c);
    const alternatives = groupOf(c) === 'pick' ? alternativesOf(c) : [];
    const options: MatchOption[] =
      alternatives.length > 0
        ? alternatives.map((r) => ({ release: r, coverUri: alternativeCoverOf(r), owned: ownedCountOf(r) > 0 ? `You own ${ownedCountOf(r)}` : null, reason: pre?.releaseId === r.id ? pre.reason : null }))
        : c.release
          ? [{ release: c.release, coverUri: alternativeCoverOf(c.release), owned: c.alreadyOwnedCount > 0 ? `You own ${c.alreadyOwnedCount}${c.ownedAt[0] ? ` · ${c.ownedAt[0]}` : ''}` : null, reason: null }]
          : [];
    const staged = review.draft.picks[c.id] ?? pre?.releaseId;
    const initialIndex = Math.max(0, options.findIndex((o) => o.release.id === staged));
    const photoIndex = place ? photos.indexOf(place.snapId) : -1;
    const thumb = thumbForLocalId(snaps, c.localId);
    return {
      c,
      kind,
      place,
      options,
      initialIndex,
      serverSkipped: !byId[c.id] && !!cardsSkipped[c.id],
      position: shelfPosition(c, [...review.list, ...Object.values(cardsSkipped)], places),
      photo: photos.length > 1 && photoIndex >= 0 ? { index: photoIndex + 1, count: photos.length } : null,
      cropUri: place?.cropUri ?? pairCropUri(c, thumb),
      ...(thumb ? { localThumb: thumb } : {}),
    };
  };

  /** Records an answer on the walk that is running. */
  const record = (answer: Answer) => {
    setDirection('forward');
    if (checking) setChecking({ ...checking, walk: answerWalk(joinWalk(checking.walk, checkIds), answer) });
    else updateWalk((w) => answerWalk(w, answer));
  };
  const onAdd = (c: CandidateDto, release: ReleaseDto | null) => {
    const prev = review.draft.checked[c.id];
    record({ id: c.id, kind: 'accepted', ...(release ? { releaseId: release.id } : {}), ...(checking ? (prev === undefined ? {} : { prevChecked: prev }) : { prevChecked: prev ?? false }) });
    void review.accept(c, release);
  };
  const onNotThis = async (c: CandidateDto) => {
    const prev = review.draft.checked[c.id];
    if (await review.skip(c)) record({ id: c.id, kind: 'skipped', ...(prev === undefined ? {} : { prevChecked: prev }) });
  };
  const onLeave = (c: CandidateDto) => {
    const prev = review.draft.checked[c.id];
    review.tick(c, false);
    record({ id: c.id, kind: 'left', ...(prev === undefined ? {} : { prevChecked: prev }) });
  };
  const onFixed = (c: CandidateDto) => {
    // Picking another match in Fix it accepts the row on the server: that is this card's answer.
    const fresh = useCardsStore.getState().byId[c.id];
    if (fresh?.decision === 'accepted' && c.decision !== 'accepted') {
      review.tick(fresh, true);
      record({ id: c.id, kind: 'accepted', prevChecked: false });
    }
  };
  const onUndo = async () => {
    setDirection('back');
    if (!checking) return review.undoLast(find);
    const { state, answer } = undoWalk(joinWalk(checking.walk, checkIds));
    if (answer && (await review.reverse(answer, find(answer.id)))) setChecking({ ...checking, walk: state });
  };
  const onCheck = (kind: Checking['kind']) => {
    setDirection('forward');
    setChecking({ kind, walk: emptyWalk() });
  };

  const extras = (
    <>
      {review.celebration ? <CommitCelebration key={review.celebration.key} message={review.celebration.message} onDone={review.dismissCelebration} /> : null}
      {review.toast ? <Snackbar message={review.toast.message} actionLabel={review.toast.canUndo ? 'Undo' : undefined} onAction={() => void review.undo()} /> : null}
    </>
  );

  if (review.list.length === 0 && review.skipped.length === 0) {
    return (
      <Screen>
        {review.loading ? <LoadingState label="Loading what you scanned…" /> : null}
        {review.loadError ? (
          <Text tone="danger" accessibilityRole="alert">
            {review.loadError}
          </Text>
        ) : null}
        {!review.loading ? <EmptyState title="Nothing to review." body="Scan something and it shows up here." animation="empty-library" /> : null}
        {extras}
      </Screen>
    );
  }

  const cardId = checking ? checkCurrent : atEnd ? null : current;
  const card = cardId ? find(cardId) : undefined;
  if (card) {
    const running = checking && checkWalk ? checkWalk : walk;
    const stillNeeds = checking ? (id: string) => checkSet.has(id) : (id: string) => needs.has(id);
    return (
      <WalkCard
        model={modelOf(card, checking?.kind ?? 'needs')}
        direction={direction}
        progress={progressLabel(progressOf(running, stillNeeds, card.id))}
        canUndo={running.stack.length > 0}
        onUndo={() => void onUndo()}
        reading={checking ? null : reading}
        review={review}
        locations={locations}
        onAdd={onAdd}
        onNotThis={(c) => void onNotThis(c)}
        onLeave={onLeave}
        onFixed={onFixed}
        onStop={checking ? () => setChecking(null) : undefined}
        onOpenPhoto={onOpenResult}
        onScanBarcode={review.canAdd ? onScanBarcode : undefined}
        onSnapCover={review.canAdd ? onSnapCover : undefined}
        width={width}
        fontScale={fontScale}
      />
    );
  }

  const answered = walk.stack;
  const ownedUnticked = review.groups.owned.filter((c) => !review.isChecked(c)).length;
  return (
    <Screen>
      {review.loadError ? (
        <Text tone="danger" accessibilityRole="alert">
          {review.loadError}
        </Text>
      ) : null}
      {(checkWalk ?? walk).stack.length > 0 && review.canAdd ? <TextAction label="Undo" accessibilityLabel="Undo the last answer" onPress={() => void onUndo()} /> : null}
      <WalkEnd
        review={review}
        locations={locations}
        counts={{
          confident: review.list.filter((c) => confident(c, review.draft) && !answered.some((a) => a.id === c.id)).length,
          picked: answered.filter((a) => a.kind === 'accepted' && !!byId[a.id]).length,
          owned: ownedUnticked,
          walked: answered.length,
        }}
        more={walk.ended ? aheadOf(walk, (id) => needs.has(id), null).length : 0}
        reading={reading}
        onMore={() => updateWalk((w) => ({ ...w, ended: false }))}
        onCheck={onCheck}
        onSeeAll={onSeeAll}
      />
      {extras}
    </Screen>
  );
}
