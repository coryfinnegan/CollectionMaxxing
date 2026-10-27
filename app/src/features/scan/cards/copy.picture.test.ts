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

  // #435: the Suddenly You record, verbatim from prod: garbled duplicated blurb as the read text,
  // the right title in the read guess, two correct catalog alternatives.
  const blurb = 'The rare author who can make you laugh and cry—the same page. ẠồThe rare author who can make you laugh and cry-on the same pageại';
  const suddenlyYou = () =>
    picture({
      resolution: 'ambiguous',
      release: null,
      guidanceCode: 'pick_match',
      score: 0.78125,
      visibleText: blurb,
      inferred: {
        kind: 'book',
        title: 'Suddenly You',
        creator: 'LISA KLEYPAs',
        edition: 'NEW YORK TIMES BESTSELLING AUTHOR',
        profile: 'LooseCartridge',
        platform: 'manualVisible',
        completeness: 'cart_only',
      } as never,
      alternatives: [
        {
          releaseId: '01a17d15-8eb5-7fea-ae3e-b36a8d89d559',
          title: 'Suddenly You',
          format: 'other',
          platform: null,
          year: 2001,
          score: 0.78125,
          creators: ['Lisa Kleypas'],
          coverUrl: 'https://covers.openlibrary.org/b/id/4344372-L.jpg',
          alreadyOwnedCount: 0,
          artworkUrl: null,
        },
        {
          releaseId: '01a17d15-8ed2-7ad5-9856-48f99e83a698',
          title: 'Suddenly You',
          format: 'other',
          platform: null,
          year: 2003,
          score: 0.78125,
          creators: ['Lisa Kleypas'],
          coverUrl: null,
          alreadyOwnedCount: 0,
          artworkUrl: null,
        },
      ] as never,
    });

  it('never presents raw OCR as the title: the Suddenly You blurb keeps its catalog title (#435)', () => {
    const copy = describeCandidate(suddenlyYou());
    expect(copy).toMatchObject({ title: 'Suddenly You', status: 'Pick one', kind: 'pick' });
    expect(copy.title).not.toContain('rare author');
  });

  it('headlines the read guess while pending, never the raw text (#435)', () => {
    const pending = describeCandidate({ ...suddenlyYou(), resolution: 'pending', alternatives: null });
    expect(pending).toMatchObject({ title: 'Suddenly You', status: 'Identifying…', kind: 'pending' });
  });

  it('calls an unreadable picture unknown instead of showing its raw text (#435)', () => {
    const copy = describeCandidate(picture({ resolution: 'not_found', release: null, visibleText: blurb, inferred: null, alternatives: null }));
    expect(copy).toMatchObject({ title: 'Unknown item', kind: 'not_found' });
  });

  it('calls a pending picture unknown until the read guess arrives (#435: raw text is never a title)', () => {
    expect(describeCandidate(picture({ resolution: 'pending', release: null, inferred: null, alternatives: null }))).toMatchObject({
      title: 'Unknown item',
      status: 'Identifying…',
      kind: 'pending',
    });
  });

  it('keeps the read guess on a not-found card, and points to Fix it', () => {
    const copy = describeCandidate(picture({ resolution: 'not_found', release: null, inferred: { title: 'Space Mutiny' } as never }));
    expect(copy).toMatchObject({ title: 'Space Mutiny', status: PICTURE_NOT_FOUND_COPY, kind: 'not_found' });
    expect(copy.status).not.toMatch(/[!]|sorry|oops/i);
  });

  it('asks to pick one on a close call, with no Add yet', () => {
    expect(describeCandidate(picture({ resolution: 'ambiguous', release: null }))).toMatchObject({ status: 'Pick one', kind: 'pick' });
  });

  it('leads a poor read with the reshoot ask, not the catalog miss', () => {
    expect(
      describeCandidate(picture({ resolution: 'not_found', release: null, guidanceCode: 'move_closer', inferred: { title: 'Space Mutiny' } as never })),
    ).toMatchObject({
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
