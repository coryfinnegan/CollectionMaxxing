// One snap's state on the segment strip (docs/16 §5): where it sits between the shutter and Review, as pure data.
import type { CandidateDto } from '@/api/generated/models';
import { isLocalCard } from '../cards/cardsStore';
import { cardFor } from '../pipeline/lines';
import type { PicSnap } from '../pipeline/pipelineStore';

export type SegmentState = 'captured' | 'reading' | 'identifying' | 'resolved' | 'needs_eye' | 'reviewed' | 'offline_queued';

/** Shoot order: oldest snap first. */
export const inShootOrder = (snaps: PicSnap[]): PicSnap[] => [...snaps].sort((a, b) => a.createdAt - b.createdAt);

export function segmentStateOf(snap: PicSnap, cards: CandidateDto[], online: boolean): SegmentState {
  if (!snap.sent) {
    if (snap.phase === 'reading') return snap.items.length === 0 ? 'captured' : 'reading';
    return online ? 'identifying' : 'offline_queued';
  }
  const waiting = !online;
  for (const item of snap.items) {
    const card = cardFor(item, cards);
    // The phone's own card is a guess from a local match: the lookup still waits until the server answers.
    if (!card || isLocalCard(card) || card.resolution === 'pending') return waiting ? 'offline_queued' : 'identifying';
  }
  const settled = snap.items.map((item) => cardFor(item, cards)!);
  if (settled.every((card) => card.createdItemId || card.decision !== 'undecided')) return 'reviewed';
  if (settled.some((card) => card.resolution === 'ambiguous' || card.resolution === 'not_found' || card.resolution === 'error'))
    return 'needs_eye';
  return 'resolved';
}

/** One card's words in the snap viewer, in sentence case. */
export function cardStateLabel(card: Pick<CandidateDto, 'resolution' | 'decision' | 'alreadyOwnedCount' | 'createdItemId'>): string {
  if (card.createdItemId) return 'Added';
  if (card.decision === 'rejected') return 'Skipped';
  if (card.resolution === 'pending') return 'Identifying';
  if (card.resolution === 'ambiguous' || card.resolution === 'not_found' || card.resolution === 'error') return 'Needs a look';
  return card.alreadyOwnedCount > 0 ? 'Owned' : 'Resolved';
}

/** The strip's words for a state, in sentence case. */
export const segmentLabel = (state: SegmentState): string =>
  (
    {
      captured: 'Captured',
      reading: 'Reading',
      identifying: 'Identifying',
      resolved: 'Resolved',
      needs_eye: 'Needs a look',
      reviewed: 'Reviewed',
      offline_queued: 'Queued',
    } as const
  )[state];

/**
 * The dot for a state, from theme roles only (the theme has no blue or green, so the working states share the ink and
 * the settled-good states share the accent; the reviewed dot is hollow). The phone never invents a color.
 */
export type SegmentDot = { fill: 'muted' | 'ink' | 'accent' | 'sticker'; hollow: boolean };

export const segmentDot = (state: SegmentState): SegmentDot => {
  switch (state) {
    case 'captured':
      return { fill: 'muted', hollow: false };
    case 'reading':
    case 'identifying':
      return { fill: 'ink', hollow: false };
    case 'resolved':
      return { fill: 'accent', hollow: false };
    case 'needs_eye':
      return { fill: 'sticker', hollow: false };
    case 'reviewed':
      return { fill: 'accent', hollow: true };
    case 'offline_queued':
      return { fill: 'muted', hollow: true };
  }
};
