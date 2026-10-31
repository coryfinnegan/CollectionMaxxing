// Strip sizing (#373): the strip fits its tallest card. A fixed height either clips tall cards (an owned card's
// buttons cut off mid-content) or wastes the camera on short ones, so each card's height is estimated from what it
// renders: header, status and error line counts, ownership answer ceilings, and button rows. Estimates bias high
// (shorter cards keep their whitespace below); the answer ceilings match the line caps, so anything past a cap
// truncates with an explicit ellipsis and content is never silently clipped. Plain data and math, so it is tested
// without rendering; the only types from ResultCards come in as types, never values, so there is no import cycle.
import type { CandidateDto } from '@/api/generated/models';
import { alternativesOf } from '@/features/review/grouping';
import { spacing, typeScale } from '@/theme/tokens';
import { describeCandidate, isPictureCandidate, type CardCopy } from './copy';
import { pickLabel } from './PickAlternatives';
import type { CardActions } from './ResultCards';

export const CARD_WIDTH = 168;

const CARD_INNER_WIDTH = CARD_WIDTH - spacing.sm * 2 - 2; // padding and border
const HALF_BUTTON_WIDTH = (CARD_INNER_WIDTH - spacing.xs) / 2; // the Add/Wishlist row
const BUTTON_MIN_HEIGHT = 44; // Button.tsx minHeight
const COVER_HEIGHT = 66; // Cover 44 wide at the 2:3 cover ratio
/** Ownership answer lines at single size; the matching caps scale with Dynamic Type. */
const ANSWER_LABEL_LINES = 3;
const STALE_LINES = 2;
/** One note line under the answer: "Marked missing". */
const NOTE_LINES = 1;
/** Latin characters per card line at single size; wide characters count double. Conservative on purpose. */
const UNITS_PER_LINE = 18;
const LABEL_LH = typeScale.label.lineHeight as number;
const CAPTION_LH = typeScale.caption.lineHeight as number;

export const clampFontScale = (fontScale: number): number => Math.min(Math.max(fontScale, 1), 3);

const wideChar = (ch: string): boolean => (ch.codePointAt(0) ?? 0) > 0xff;

function textLines(text: string, width: number, scale: number): number {
  let units = 0;
  for (const ch of text) units += wideChar(ch) ? 2 : 1;
  const perLine = Math.max(1, Math.floor(((UNITS_PER_LINE * width) / CARD_INNER_WIDTH / scale)));
  return Math.max(1, Math.ceil(units / perLine));
}

function buttonHeight(label: string, scale: number, width: number = CARD_INNER_WIDTH): number {
  return Math.max(BUTTON_MIN_HEIGHT, textLines(label, width, scale) * LABEL_LH * scale);
}

/** Line caps matching the estimator ceilings, so a cap only binds past what the strip already fits. */
export const answerLineCap = (scale: number): number => Math.ceil(ANSWER_LABEL_LINES * scale);
export const staleLineCap = (scale: number): number => Math.ceil(STALE_LINES * scale);
export const noteLineCap = (scale: number): number => Math.ceil(NOTE_LINES * scale);

function estimateCardHeight(candidate: CandidateDto, actions: CardActions, scale: number): number {
  const copy: CardCopy = describeCandidate(candidate);
  const labelLH = LABEL_LH * scale;
  const captionLH = CAPTION_LH * scale;
  const titleH = Math.min(2, textLines(copy.title, CARD_INNER_WIDTH, scale)) * labelLH;
  const header = Math.max(COVER_HEIGHT, titleH + (copy.subtitle ? spacing.xs + captionLH : 0));
  // The box: top pad and header, then each section with the gap above it, then bottom pad and border.
  let height = spacing.sm + header;
  const section = (h: number): void => {
    height += spacing.sm + h;
  };
  const answerBlock = (): number => answerLineCap(scale) * labelLH + (staleLineCap(scale) + noteLineCap(scale)) * captionLH;
  const accepted = candidate.decision === 'accepted';
  if (copy.kind === 'owned') {
    section(answerBlock());
    if (accepted) section(textLines('Added to this scan', CARD_INNER_WIDTH, scale) * labelLH);
    if (actions.canAdd && !accepted) {
      const buttons = [buttonHeight('Add another copy', scale)];
      if (actions.onMove && actions.moveTo) buttons.push(buttonHeight('Move it here', scale));
      if (actions.onSkip) buttons.push(buttonHeight('Skip', scale));
      section(buttons.reduce((sum, h) => sum + h, 0) + spacing.xs * Math.max(0, buttons.length - 1));
    }
  } else if (copy.kind === 'wishlist') {
    // "On your wishlist", then Got it and Skip (or, once moved, the new line and Undo: never taller).
    section(textLines(copy.status ?? '', CARD_INNER_WIDTH, scale) * labelLH);
    if (actions.canAdd) {
      const buttons = [buttonHeight('Got it', scale)];
      if (actions.onSkip) buttons.push(buttonHeight('Skip', scale));
      section(buttons.reduce((sum, h) => sum + h, 0) + spacing.xs * Math.max(0, buttons.length - 1));
    }
    const error = actions.errors?.[candidate.id];
    if (error) section(textLines(error, CARD_INNER_WIDTH, scale) * captionLH);
  } else {
    if (copy.status) section(textLines(copy.status, CARD_INNER_WIDTH, scale) * labelLH);
    if ((copy.kind === 'pending' || copy.kind === 'error') && candidate.rawCode) section(answerBlock());
    if (copy.kind === 'new' && candidate.release) section(answerBlock());
    if (copy.kind === 'added' && actions.onUndoAutoCommit) section(buttonHeight('Undo', scale));
    if (copy.kind === 'added' && candidate.tier === 'T1' && candidate.createdItemId && actions.onVerifyEdition)
      section(buttonHeight('Verify edition', scale));
    if (actions.canAdd && actions.onFixIt && isPictureCandidate(candidate) && (copy.kind === 'not_found' || copy.kind === 'error'))
      section(buttonHeight('Fix it', scale));
    if (actions.canAdd && actions.onPick && copy.kind === 'pick' && alternativesOf(candidate).length > 0) {
      const picks = alternativesOf(candidate).map((r) => buttonHeight(pickLabel(r), scale));
      section(picks.reduce((sum, h) => sum + h, 0) + spacing.sm * Math.max(0, picks.length - 1));
    }
    const addable = actions.canAdd && copy.kind === 'new' && !!candidate.release;
    const wishlisted = !!actions.wishlisted?.[candidate.id];
    if (addable && (accepted || wishlisted))
      section(textLines(wishlisted ? 'On your wishlist' : 'Added to this scan', CARD_INNER_WIDTH, scale) * labelLH);
    if (addable && !accepted && !wishlisted)
      section(Math.max(buttonHeight('+ Add', scale, HALF_BUTTON_WIDTH), buttonHeight('Wishlist', scale, HALF_BUTTON_WIDTH)));
    const error = actions.errors?.[candidate.id];
    if (error) section(textLines(error, CARD_INNER_WIDTH, scale) * captionLH);
  }
  return Math.ceil(height + spacing.sm + 2); // bottom pad and border
}

export function estimateStripHeight(cards: CandidateDto[], actions: CardActions, fontScale: number): number {
  const scale = clampFontScale(fontScale);
  return cards.reduce((tallest, card) => Math.max(tallest, estimateCardHeight(card, actions, scale)), 0);
}
