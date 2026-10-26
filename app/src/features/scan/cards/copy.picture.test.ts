import type { CandidateDto } from '@/api/generated/models';
import { describeCandidate, isPictureCandidate, PICTURE_NOT_FOUND_COPY } from './copy';

const picture = (over: Partial<CandidateDto>): CandidateDto =>
  ({
    id: 'p1',
    seq: 1,
    source: 'picture_apple_model',
    rawCode: null,
    visibleText: 'Space Mutiny',
    resolution: 'matched',
    alreadyOwnedCount: 0,
    ownedAt: [],
    release: { id: 'r', title: 'Space Mutiny', format: 'vhs', year: 1988, platform: null, creators: [] },
    ...over,
  }) as CandidateDto;

describe('what a picture card says', () => {
  it('tells pictures from barcodes by where the candidate came from', () => {
    expect(isPictureCandidate(picture({}))).toBe(true);
    expect(isPictureCandidate(picture({ source: 'picture_text' }))).toBe(true);
    expect(isPictureCandidate(picture({ source: 'barcode' }))).toBe(false);
  });

  it('shows what was read while the server is still identifying it', () => {
    expect(describeCandidate(picture({ resolution: 'pending', release: null }))).toMatchObject({ title: 'Space Mutiny', status: 'Identifying…', kind: 'pending' });
  });

  it('keeps the read text on a not-found card, and points to Fix it', () => {
    const copy = describeCandidate(picture({ resolution: 'not_found', release: null }));
    expect(copy).toMatchObject({ title: 'Space Mutiny', status: PICTURE_NOT_FOUND_COPY, kind: 'not_found' });
    expect(copy.status).not.toMatch(/[!]|sorry|oops/i);
  });

  it('asks to pick one on a close call, with no Add yet', () => {
    expect(describeCandidate(picture({ resolution: 'ambiguous', release: null }))).toMatchObject({ status: 'Pick one', kind: 'pick' });
  });

  it('leads a poor read with the reshoot ask, not the catalog miss', () => {
    expect(describeCandidate(picture({ resolution: 'not_found', release: null, guidanceCode: 'move_closer' }))).toMatchObject({
      title: 'Space Mutiny',
      status: 'Move closer — spines too small',
      kind: 'not_found',
    });
  });

  it('keeps its own words when the guidance only repeats the fallback', () => {
    expect(describeCandidate(picture({ resolution: 'not_found', release: null, guidanceCode: 'shoot_cover' }))).toMatchObject({
      status: PICTURE_NOT_FOUND_COPY,
      kind: 'not_found',
    });
  });

  it('leads with "Already own" even when the match is a close call', () => {
    expect(describeCandidate(picture({ resolution: 'ambiguous', alreadyOwnedCount: 2 }))).toMatchObject({ status: 'Already own — VHS · 2 copies', kind: 'owned' });
    expect(describeCandidate(picture({ alreadyOwnedCount: 1 }))).toMatchObject({ title: 'Space Mutiny', subtitle: 'VHS · 1988', status: 'Already own — VHS', kind: 'owned' });
  });

  it('is a new item, ready to add, when it matched and the library does not have it', () => {
    expect(describeCandidate(picture({}))).toMatchObject({ status: null, kind: 'new' });
  });
});
