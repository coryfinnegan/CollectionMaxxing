// One card of the walk (#833), top to bottom: progress, your photo with the item lit, the item itself, the match with a large cover,
// and the answers pinned in the thumb zone. Buttons decide; a swipe only flips between matches. On each answer the card slides
// away and the next comes in, while the photo glides to the next item. Reduce Motion swaps the card at once.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { AccessibilityInfo, ScrollView, View, type LayoutChangeEvent } from 'react-native';
import Animated, { useAnimatedStyle, useSharedValue, withTiming } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto, LocationDto, ReleaseDto } from '@/api/generated/models';
import { FixItPanel } from '@/features/scan/detail/FixItPanel';
import { PHOTO_GONE } from '@/features/scan/detail/PhotoViewer';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { haptics } from '@/lib/haptics';
import { spacing } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import { motionMs } from '@/ui/motion';
import { groupOf } from './grouping';
import { COVER_WIDTH, COVER_WIDTH_LARGE_TEXT, LARGE_TEXT_SCALE, MatchReel, type MatchOption } from './MatchReel';
import { isTall, UNMARKED_LINE, type Place } from './placement';
import { keepable } from './provisionals';
import { ScanBarcodeButton, SnapCoverButton } from './ReviewList';
import { CandidateFields, titleOf, type Review } from './RowSheets';
import { CropStrip, ShelfBand, SpineStrip } from './ShelfWindow';
import { TextAction } from './TextAction';
import { ordinal } from './walk';

/** What a walk is for: the rows that need you, the confident rows ("Check them"), or the rows you own. */
export type WalkKind = 'needs' | 'confident' | 'owned';

export type CardModel = {
  c: CandidateDto;
  kind: WalkKind;
  place: Place | null;
  options: MatchOption[];
  /** The match the card opens on: the preselected one, else the first. */
  initialIndex: number;
  /** The server skipped this row while it was on screen: it stays, and Add this overrides the skip. */
  serverSkipped: boolean;
  /** Its place on the shelf, from the left. */
  position: number | null;
  /** "Photo 2 of 3", when the walk spans more than one photo. */
  photo: { index: number; count: number } | null;
  /** The crop to fall back on when the still is gone: the phone's own, else the server's. */
  cropUri: string | null;
  /** The phone's own crop: it goes up with Keep as unverified, and only then. */
  localThumb?: string;
};

export const SERVER_SKIPPED_LINE = 'The server read this shelf differently';
export const NOTHING_MATCHED = 'Nothing in the catalog matched';
const STRIP_MAX = 96;
const STRIP_MAX_NO_COVER = 140;

export function WalkCard({
  model,
  direction,
  progress,
  canUndo,
  onUndo,
  reading,
  review,
  locations,
  onAdd,
  onNotThis,
  onLeave,
  onFixed,
  onStop,
  onOpenPhoto,
  onScanBarcode,
  onSnapCover,
  width,
  fontScale,
}: {
  model: CardModel;
  /** Forward slides the card left; back (Undo) slides it right. */
  direction: 'forward' | 'back';
  progress: string | null;
  canUndo: boolean;
  onUndo(): void;
  /** "Still reading 1 photo. Its finds join the end." */
  reading: string | null;
  review: Review;
  locations: LocationDto[];
  onAdd(c: CandidateDto, release: ReleaseDto | null): void;
  onNotThis(c: CandidateDto): void;
  onLeave(c: CandidateDto): void;
  /** Fix it closed: the row may have changed under the card. */
  onFixed(c: CandidateDto): void;
  /** Ends a Check them walk. */
  onStop?: () => void;
  onOpenPhoto(c: CandidateDto): void;
  onScanBarcode?: (c: CandidateDto) => void;
  onSnapCover?: (c: CandidateDto) => void;
  width: number;
  fontScale: number;
}) {
  const reduceMotion = useReduceMotion();
  const [measured, setMeasured] = useState(0);
  const bandWidth = measured || width;
  const inner = Math.max(0, bandWidth - spacing.lg * 2);

  // The card on screen. While the next one is coming the old one finishes leaving (as it looked when it came in); the card on
  // screen always shows its fresh data. Under Reduce Motion the next card is simply there.
  const [snapshot, setSnapshot] = useState(model);
  const latest = useRef(model);
  useEffect(() => {
    latest.current = model;
  });
  const moving = !reduceMotion && snapshot.c.id !== model.c.id;
  const showing = moving ? snapshot : model;
  const slide = useSharedValue(0);
  const fade = useSharedValue(1);
  const slideStyle = useAnimatedStyle(() => ({ opacity: fade.value, transform: [{ translateX: slide.value }] }), []);
  const cardId = model.c.id;
  useEffect(() => {
    if (snapshot.c.id === cardId) return;
    if (reduceMotion) {
      const timer = setTimeout(() => setSnapshot(latest.current), 0);
      return () => clearTimeout(timer);
    }
    const away = (direction === 'back' ? 1 : -1) * Math.max(80, inner * 0.4);
    // Shared values are mutated by design; this is Reanimated's documented pattern.
    /* eslint-disable react-hooks/immutability */
    slide.value = withTiming(away, { duration: motionMs.base });
    fade.value = withTiming(0, { duration: motionMs.base });
    const timer = setTimeout(() => {
      setSnapshot(latest.current);
      slide.value = -away;
      fade.value = 0;
      slide.value = withTiming(0, { duration: motionMs.base });
      fade.value = withTiming(1, { duration: motionMs.base });
    }, motionMs.base);
    /* eslint-enable react-hooks/immutability */
    return () => clearTimeout(timer);
    // The move starts when the card changes, not when the same card gets fresher data.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cardId, reduceMotion]);

  // Each new card: VoiceOver goes to the match.
  const matchRef = useRef<View>(null);
  const titleRef = useRef<View>(null);
  useEffect(() => {
    const target = matchRef.current ?? titleRef.current;
    if (target) AccessibilityInfo.sendAccessibilityEvent(target, 'focus');
  }, [showing.c.id]);

  // Which match the reel shows, per card.
  const [choice, setChoice] = useState<{ id: string; index: number } | null>(null);
  const index = choice?.id === showing.c.id ? choice.index : showing.initialIndex;
  const [fixFor, setFixFor] = useState<string | null>(null);
  const [pickingFor, setPickingFor] = useState<string | null>(null);
  const scroll = useRef<ScrollView>(null);

  const { c, place, options } = showing;
  const title = titleOf(c);
  const option = options[index] ?? options[0] ?? null;
  const pick = groupOf(c) === 'pick';
  const notFound = !option;
  const fixing = fixFor === c.id;
  const busy = !!review.rowBusy[c.id] || moving;
  const still = place?.still;
  const box = place?.box;
  const tall = !!(still && box && isTall(box, still));
  const large = fontScale >= LARGE_TEXT_SCALE;
  // No cover art: a cover shot fills the slot with the phone's own picture; a spine lets the strip grow instead.
  const noArt = !!option && !option.coverUri;
  const ownPicture = noArt && still && box && !tall ? <SpineStrip still={still} box={box} max={{ width: large ? COVER_WIDTH_LARGE_TEXT : COVER_WIDTH, height: 240 }} label="Your photo" /> : null;
  const stripMax = { width: inner, height: noArt || notFound ? STRIP_MAX_NO_COVER : STRIP_MAX };

  const onIndex = (next: number) => {
    setChoice({ id: c.id, index: next });
    void haptics.select();
    const release = options[next]?.release;
    if (pick && release) review.select(c, release); // stages the pick; only Add this decides
  };
  const openFix = () => {
    setFixFor(c.id);
    setTimeout(() => scroll.current?.scrollToEnd({ animated: !reduceMotion }), 0);
  };
  const owned = showing.kind === 'owned';
  const error = review.rowErrors[c.id];

  return (
    <View style={styles.root} onLayout={(e: LayoutChangeEvent) => setMeasured(e.nativeEvent.layout.width)}>
      <ScrollView ref={scroll} style={styles.scroll} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <View style={styles.head}>
          <View style={styles.progressRow}>
            {progress ? (
              <Text variant="label" accessibilityLiveRegion="polite">
                {progress}
              </Text>
            ) : null}
            {canUndo ? (
              <>
                {progress ? (
                  <Text variant="label" tone="muted" accessibilityElementsHidden importantForAccessibility="no">
                    ·
                  </Text>
                ) : null}
                <TextAction label="Undo" accessibilityLabel="Undo the last answer" disabled={moving} onPress={onUndo} />
              </>
            ) : null}
            {onStop ? (
              <View style={styles.push}>
                <TextAction label="Stop checking" onPress={onStop} />
              </View>
            ) : null}
          </View>
          {reading ? (
            <Text variant="caption" tone="muted" accessibilityLiveRegion="polite">
              {reading}
            </Text>
          ) : null}
          {model.photo && model.place?.still ? (
            <Text variant="caption" tone="muted">
              {`Photo ${model.photo.index} of ${model.photo.count}`}
            </Text>
          ) : null}
        </View>

        {/* The band follows the card that is coming, so it glides while the old card leaves. */}
        {model.place?.still ? (
          <ShelfBand
            snapId={model.place.snapId}
            still={model.place.still}
            box={model.place.box}
            width={bandWidth}
            label={model.place.box ? `Your photo, item highlighted${model.position ? `, ${ordinal(model.position)} from left` : ''}` : 'Your photo'}
            onOpen={() => onOpenPhoto(model.c)}
          />
        ) : null}

        <Animated.View style={[styles.body, slideStyle]}>
          {place && !still ? <Text tone="muted">{PHOTO_GONE}</Text> : null}
          {place?.still && !place.box ? <Text tone="muted">{UNMARKED_LINE}</Text> : null}
          {showing.serverSkipped ? <Text tone="muted">{SERVER_SKIPPED_LINE}</Text> : null}
          {still && box && !ownPicture ? <SpineStrip still={still} box={box} max={stripMax} /> : null}
          {!still && showing.cropUri ? <CropStrip title={title} uri={showing.cropUri} /> : null}

          {option ? (
            <MatchReel ref={matchRef} options={options} index={index} onIndex={onIndex} ownPicture={ownPicture ? <OwnPicture>{ownPicture}</OwnPicture> : undefined} fontScale={fontScale} />
          ) : (
            <View ref={titleRef} accessible accessibilityLabel={`${title}. ${NOTHING_MATCHED}`} style={styles.notFound}>
              <Text variant="heading">{title}</Text>
              {c.visibleText ? <Text variant="mono">{`Read as “${c.visibleText}”`}</Text> : null}
              <Text tone="muted">{NOTHING_MATCHED}</Text>
            </View>
          )}

          {review.queued[c.id] ? <Text tone="muted">Kept. Sends when you are back online.</Text> : null}
          {error ? (
            <Text tone="danger" accessibilityRole="alert">
              {error}
            </Text>
          ) : null}

          {fixing && review.canAdd ? (
            <View style={styles.fix}>
              <FixItPanel
                candidate={c}
                sessionId={review.sessionId}
                onClose={() => {
                  setFixFor(null);
                  onFixed(c);
                }}
              />
              <View style={styles.extras}>
                {onSnapCover ? <SnapCoverButton title={title} onPress={() => onSnapCover(c)} /> : null}
                {onScanBarcode ? <ScanBarcodeButton title={title} onPress={() => onScanBarcode(c)} /> : null}
                {keepable(c) ? <Button label="Keep as unverified" accessibilityLabel={`Keep ${title} as unverified`} variant="secondary" busy={busy} onPress={() => void review.keep(c, showing.localThumb)} /> : null}
                {c.release ? <CandidateFields c={c} review={review} locations={locations} pickingFor={pickingFor} setPickingFor={setPickingFor} /> : null}
                <Button label="Discard" accessibilityLabel={`Discard ${title}`} variant="danger" busy={busy} onPress={() => void review.discard(c)} />
              </View>
            </View>
          ) : null}
        </Animated.View>
      </ScrollView>

      {review.canAdd ? (
        <View style={styles.actions}>
          {owned ? (
            <>
              <Button label="Add another copy" accessibilityLabel={`Add another copy of ${title}`} haptic="commit" disabled={busy} style={styles.primary} onPress={() => onAdd(c, null)} />
              <View style={styles.secondaryRow}>
                <Button label="Leave it" accessibilityLabel={`Leave ${title}`} variant="secondary" disabled={busy} style={styles.grow} onPress={() => onLeave(c)} />
              </View>
            </>
          ) : (
            <>
              {notFound ? (
                <Button label="Search" accessibilityLabel={`Search for ${title}`} disabled={busy} style={styles.primary} onPress={fixing ? () => setFixFor(null) : openFix} />
              ) : (
                <Button label="Add this" accessibilityLabel={`Add this: ${option.release.title}`} haptic="commit" disabled={busy} style={styles.primary} onPress={() => onAdd(c, pick ? option.release : null)} />
              )}
              <View style={styles.secondaryRow}>
                <Button label="Not this one" accessibilityLabel={`Not this one: ${title}`} variant="secondary" disabled={busy} style={styles.grow} onPress={() => onNotThis(c)} />
                {notFound ? null : <TextAction label="Fix it" accessibilityLabel={`Fix ${title}`} disabled={moving} onPress={fixing ? () => setFixFor(null) : openFix} />}
              </View>
            </>
          )}
        </View>
      ) : null}
    </View>
  );
}

/** The phone's own picture standing in for missing cover art, captioned so it is never mistaken for the catalog's. */
function OwnPicture({ children }: { children: ReactNode }) {
  return (
    <View style={styles.own}>
      {children}
      <Text variant="caption" tone="muted">
        Your photo
      </Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { flex: 1, backgroundColor: theme.colors.ground },
  scroll: { flex: 1 },
  content: { paddingBottom: theme.spacing.lg, gap: theme.spacing.md },
  head: { paddingHorizontal: theme.spacing.lg, paddingTop: theme.spacing.md, gap: theme.spacing.xs },
  progressRow: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.xs, minHeight: 44 },
  push: { marginLeft: 'auto' },
  body: { paddingHorizontal: theme.spacing.lg, gap: theme.spacing.md },
  notFound: { gap: theme.spacing.xs },
  fix: { gap: theme.spacing.md },
  extras: { gap: theme.spacing.sm },
  own: { gap: theme.spacing.xs, alignItems: 'center' },
  actions: {
    paddingHorizontal: theme.spacing.lg,
    paddingTop: theme.spacing.md,
    paddingBottom: theme.spacing.md,
    gap: theme.spacing.sm,
    borderTopWidth: 1,
    borderTopColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
  primary: { minHeight: 52 },
  secondaryRow: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  grow: { flex: 1 },
}));
