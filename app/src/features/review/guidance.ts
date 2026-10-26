// What an unresolved row tells the person to do next (design §8(a)): the server's
// guidance code, in the person's words. Resolved rows carry no code and say nothing.
import type { CandidateDto, GuidanceCode } from '@/api/generated/models';

type KnownGuidance = Exclude<GuidanceCode, null>;

const COPY: Record<KnownGuidance, string> = {
  move_closer: 'Move closer — spines too small',
  split_segment: 'Split the segment — shoot left/right halves',
  hold_steady: 'Hold steady — add light',
  kill_glare: 'Tilt to kill the glare',
  shoot_cover: 'Shoot the front cover',
  pick_match: 'Pick the right one',
  scan_barcode: 'Scan the barcode again',
};

const isReshoot = (code: KnownGuidance): boolean =>
  code === 'move_closer' || code === 'split_segment' || code === 'hold_steady' || code === 'kill_glare';

export const guidanceCopy = (code: KnownGuidance): string => COPY[code];

/** The row's specific next step, or null when the row is resolved. */
export const guidanceOf = (c: Pick<CandidateDto, 'guidanceCode'>): string | null => (c.guidanceCode ? COPY[c.guidanceCode] : null);

/** The copy when the classifier asked for a reshoot, else null (the fallbacks keep their own words on cards). */
export const reshootOf = (c: Pick<CandidateDto, 'guidanceCode'>): string | null =>
  c.guidanceCode && isReshoot(c.guidanceCode) ? COPY[c.guidanceCode] : null;
