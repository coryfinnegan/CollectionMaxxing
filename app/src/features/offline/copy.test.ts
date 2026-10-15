import { NEVER_SYNCED, staleCopyMessage, syncAge } from './copy';

const MIN = 60_000;
const H = 60 * MIN;

describe('syncAge', () => {
  it.each([
    [0, 'just now'],
    [59_000, 'just now'],
    [MIN, '1 min ago'],
    [45 * MIN, '45 min ago'],
    [H, '1 h ago'],
    [2 * H + 10 * MIN, '2 h ago'],
    [23 * H, '23 h ago'],
    [24 * H, '1 d ago'],
    [72 * H, '3 d ago'],
  ])('%i ms is %s', (elapsed, text) => {
    expect(syncAge(1_000_000, 1_000_000 + elapsed)).toBe(text);
  });

  it('never goes negative when the clock moved back', () => {
    expect(syncAge(2_000_000, 1_000_000)).toBe('just now');
  });
});

describe('staleCopyMessage', () => {
  it('shows the age of the last sync', () => {
    expect(staleCopyMessage(0, 2 * H)).toBe('Based on your last sync, 2 h ago');
  });

  it('says what to do when there has never been a sync', () => {
    expect(staleCopyMessage(null, 5)).toBe(NEVER_SYNCED);
    expect(NEVER_SYNCED).not.toMatch(/!|oops|sorry/i);
  });
});
