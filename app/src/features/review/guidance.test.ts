// Every unresolved row names its specific next step (design §8(a)): the server's
// guidance code, in the person's words. Sentence case, no exclamation marks.
import type { GuidanceCode } from '@/api/generated/models';
import { guidanceCopy, guidanceOf, reshootOf } from './guidance';
import { candidate } from './testData';

const EVERY_CODE: [Exclude<GuidanceCode, null>, string][] = [
  ['move_closer', 'Move closer — spines too small'],
  ['split_segment', 'Split the segment — shoot left/right halves'],
  ['hold_steady', 'Hold steady — add light'],
  ['kill_glare', 'Tilt to kill the glare'],
  ['shoot_cover', 'Shoot the front cover'],
  ['pick_match', 'Pick the right one'],
  ['scan_barcode', 'Scan the barcode again'],
];

describe('row guidance', () => {
  it.each(EVERY_CODE)('says %s as "%s"', (code, copy) => {
    expect(guidanceCopy(code)).toBe(copy);
  });

  it('reads the copy off the row, and nothing once the row is resolved', () => {
    expect(guidanceOf(candidate({ guidanceCode: 'move_closer' }))).toBe('Move closer — spines too small');
    expect(guidanceOf(candidate({ guidanceCode: null }))).toBeNull();
  });

  it('picks out the reshoot signals from the fallbacks', () => {
    expect(reshootOf(candidate({ guidanceCode: 'kill_glare' }))).toBe('Tilt to kill the glare');
    expect(reshootOf(candidate({ guidanceCode: 'shoot_cover' }))).toBeNull();
    expect(reshootOf(candidate({ guidanceCode: 'pick_match' }))).toBeNull();
    expect(reshootOf(candidate({ guidanceCode: 'scan_barcode' }))).toBeNull();
    expect(reshootOf(candidate({ guidanceCode: null }))).toBeNull();
  });
});
