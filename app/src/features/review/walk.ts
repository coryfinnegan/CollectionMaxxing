// Review one at a time (#833), as pure functions: which rows need the person, the order they are walked in (photo by photo,
// left to right along the shelf), and the walk's own state (the order fixed so far, the answers given, the card on screen).
import type { CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import { defaultChecked, groupOf } from './grouping';
import type { Draft } from './plan';
import type { Place } from './placement';

/**
 * A row the person has to look at: Pick one, Not found, and a Ready row that is not ticked by itself (an unconfirmed read, which
 * includes every server row). A ready row the person ticked themselves no longer needs them.
 */
export function needsYou(c: CandidateDto, draft: Draft): boolean {
  const group = groupOf(c);
  if (group === 'pick' || group === 'notFound') return true;
  return group === 'ready' && !defaultChecked(c, group) && draft.checked[c.id] !== true;
}

/** A Ready row that matched by itself: it is added with the rest unless the person says otherwise. */
export function confident(c: CandidateDto, draft: Draft): boolean {
  return groupOf(c) === 'ready' && !needsYou(c, draft);
}

const centerX = (b: Rect) => b.x + b.width / 2;

/** How much two boxes overlap sideways, as a share of the narrower one. */
const sideOverlap = (a: Rect, b: Rect): number => {
  const overlap = Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x);
  return overlap / Math.max(1e-9, Math.min(a.width, b.width));
};

/**
 * Shelf order: left to right by each box's center. Boxes that overlap sideways by more than half the narrower one are a stack,
 * read top to bottom, and the stack takes its place in the row by its leftmost center.
 */
export function shelfOrder<T>(entries: readonly { item: T; box: Rect }[]): T[] {
  const columns: { item: T; box: Rect }[][] = [];
  for (const entry of [...entries].sort((a, b) => centerX(a.box) - centerX(b.box))) {
    const column = columns.find((col) => col.some((other) => sideOverlap(other.box, entry.box) > 0.5));
    if (column) column.push(entry);
    else columns.push([entry]);
  }
  return columns
    .map((col) => ({ x: Math.min(...col.map((e) => centerX(e.box))), col: [...col].sort((a, b) => a.box.y - b.box.y) }))
    .sort((a, b) => a.x - b.x)
    .flatMap(({ col }) => col.map((e) => e.item));
}

/**
 * The order rows are walked in: photos in the order they were taken; inside a photo the rows with a box in shelf order, then the
 * photo's rows with no box in the server's order (`seq`); rows from no photo last, in the server's order.
 */
export function orderRows(rows: readonly CandidateDto[], places: Readonly<Record<string, Place | null | undefined>>): CandidateDto[] {
  const bySeq = (a: CandidateDto, b: CandidateDto) => a.seq - b.seq;
  const photos = new Map<string, { takenAt: number; rows: CandidateDto[] }>();
  const loose: CandidateDto[] = [];
  for (const c of rows) {
    const place = places[c.id];
    if (!place) {
      loose.push(c);
      continue;
    }
    const photo = photos.get(place.snapId) ?? { takenAt: place.takenAt, rows: [] };
    photo.rows.push(c);
    photos.set(place.snapId, photo);
  }
  const ordered = [...photos.values()]
    .sort((a, b) => a.takenAt - b.takenAt)
    .flatMap((photo) => {
      const boxed = photo.rows.filter((c) => places[c.id]?.box);
      const unboxed = photo.rows.filter((c) => !places[c.id]?.box).sort(bySeq);
      return [...shelfOrder(boxed.map((c) => ({ item: c, box: places[c.id]!.box! }))), ...unboxed];
    });
  return [...ordered, ...loose.sort(bySeq)];
}

/** The rows that need the person, in walking order. */
export function walkQueue(list: readonly CandidateDto[], draft: Draft, places: Readonly<Record<string, Place | null | undefined>>): CandidateDto[] {
  return orderRows(
    list.filter((c) => needsYou(c, draft)),
    places,
  );
}

/** One answer on a card. `prevChecked` is the row's explicit tick before the answer (absent: it followed its default). */
export interface Answer {
  id: string;
  kind: 'accepted' | 'skipped' | 'left';
  /** The edition chosen on a pick-one card. */
  releaseId?: string;
  prevChecked?: boolean;
}

/** A walk: the rows in the order they joined (fixed once joined, so a late row never jumps the queue), the answers, and the card on screen. */
export interface WalkState {
  order: string[];
  stack: Answer[];
  /** The card on screen, kept across Fix it, a trip to the camera and a restart. Null: the first unanswered row. */
  place: string | null;
  /** The person reached the end: rows that join later wait behind "N more to check". */
  ended: boolean;
}

export const emptyWalk = (): WalkState => ({ order: [], stack: [], place: null, ended: false });

/** Adds rows that need the person and are not in the walk yet, at its end. */
export function joinWalk(state: WalkState, ids: readonly string[]): WalkState {
  const known = new Set(state.order);
  const fresh = ids.filter((id) => !known.has(id));
  return fresh.length === 0 ? state : { ...state, order: [...state.order, ...fresh] };
}

export function answerWalk(state: WalkState, answer: Answer): WalkState {
  return { ...state, stack: [...state.stack.filter((a) => a.id !== answer.id), answer], place: null };
}

/** Steps back one card: the last answer comes off and its card is the one on screen again. */
export function undoWalk(state: WalkState): { state: WalkState; answer: Answer | undefined } {
  const answer = state.stack[state.stack.length - 1];
  if (!answer) return { state, answer };
  return { state: { ...state, stack: state.stack.slice(0, -1), place: answer.id, ended: false }, answer };
}

/**
 * The card on screen: the kept place while it is unanswered and the row still exists (a card the server skipped, or one Fix it
 * matched, stays put), else the first unanswered row that still needs the person.
 */
export function currentOf(state: WalkState, stillNeeds: (id: string) => boolean, exists: (id: string) => boolean): string | null {
  const answered = new Set(state.stack.map((a) => a.id));
  if (state.place && !answered.has(state.place) && exists(state.place) && state.order.includes(state.place)) return state.place;
  return state.order.find((id) => !answered.has(id) && stillNeeds(id)) ?? null;
}

/** The rows still ahead, the one on screen included. */
export function aheadOf(state: WalkState, stillNeeds: (id: string) => boolean, current: string | null): string[] {
  const answered = new Set(state.stack.map((a) => a.id));
  return state.order.filter((id) => !answered.has(id) && (stillNeeds(id) || id === current));
}

/** "4 of 12": the answered cards plus the ones ahead. */
export function progressOf(state: WalkState, stillNeeds: (id: string) => boolean, current: string | null): { index: number; total: number } {
  const answered = state.stack.filter((a) => state.order.includes(a.id)).length;
  return { index: answered + 1, total: answered + aheadOf(state, stillNeeds, current).length };
}

export const progressLabel = ({ index, total }: { index: number; total: number }): string | null => (total > 1 ? `${index} of ${total}` : null);

/** "4th": the item's place on the shelf, for VoiceOver. */
export function ordinal(n: number): string {
  const tens = n % 100;
  if (tens >= 11 && tens <= 13) return `${n}th`;
  const suffix = ({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[n % 10] ?? 'th';
  return `${n}${suffix}`;
}

/** Where a row sits on its shelf among every boxed row of the same photo, counted from the left (1-based), or null without a box. */
export function shelfPosition(c: CandidateDto, list: readonly CandidateDto[], places: Readonly<Record<string, Place | null | undefined>>): number | null {
  const place = places[c.id];
  if (!place?.box) return null;
  const peers = list.filter((other) => places[other.id]?.snapId === place.snapId && places[other.id]?.box);
  const order = shelfOrder(peers.map((other) => ({ item: other.id, box: places[other.id]!.box! })));
  const index = order.indexOf(c.id);
  return index < 0 ? null : index + 1;
}

/** The line while a photo is still being read: "Still reading 1 photo. Its finds join the end." */
export const stillReadingLine = (photos: number): string | null =>
  photos <= 0 ? null : photos === 1 ? 'Still reading 1 photo. Its finds join the end.' : `Still reading ${photos} photos. Their finds join the end.`;

/** On the end screen when rows joined after it: "2 more to check". */
export const moreToCheckLabel = (n: number): string => `${n} more to check`;
