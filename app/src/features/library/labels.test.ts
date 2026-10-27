import { formatLabels } from './labels';

describe('formatLabels', () => {
  it('names the new book formats in plain words', () => {
    expect(formatLabels.board_book).toBe('Board book');
    expect(formatLabels.spiral_bound).toBe('Spiral-bound');
  });

  it('says unknown plainly, and keeps other as a real category', () => {
    expect(formatLabels.unknown).toBe('Unknown');
    expect(formatLabels.other).toBe('Other');
  });
});
