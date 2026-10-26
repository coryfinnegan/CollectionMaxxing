import { quotaPausedLine, quotaPending } from './quotaLine';
import { candidate } from './testData';

describe('quota paused line', () => {
  it('is null when nothing waits for the quota', () => {
    expect(quotaPausedLine(0)).toBeNull();
  });

  it('reads "quota paused — N pending"', () => {
    expect(quotaPausedLine(1)).toBe('quota paused — 1 pending');
    expect(quotaPausedLine(3)).toBe('quota paused — 3 pending');
  });

  it('counts quota-pending rows, not other pending rows', () => {
    const list = [
      candidate({ resolution: 'pending', pendingReason: 'quota' }),
      candidate({ resolution: 'pending', pendingReason: 'quota' }),
      candidate({ resolution: 'pending', pendingReason: 'lookup' }),
      candidate({ resolution: 'matched' }),
    ];
    expect(quotaPending(list)).toHaveLength(2);
  });
});
