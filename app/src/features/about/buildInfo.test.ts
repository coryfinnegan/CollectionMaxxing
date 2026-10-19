import { buildLine } from './buildInfo';

describe('buildLine', () => {
  it('names the short commit and the day it was built', () => {
    expect(buildLine('559f42cb22eb933635227a6cee56550c8dae2253', '2026-10-19T12:00:00Z')).toBe('Build 559f42c from Oct 19, 2026');
  });

  it('names just the commit when the build time is missing or unreadable', () => {
    expect(buildLine('559f42cb22eb933635227a6cee56550c8dae2253', undefined)).toBe('Build 559f42c');
    expect(buildLine('559f42cb22eb933635227a6cee56550c8dae2253', 'not a date')).toBe('Build 559f42c');
  });

  it('shows nothing for a local or test build, which has no commit', () => {
    expect(buildLine(undefined, '2026-10-19T12:00:00Z')).toBeNull();
    expect(buildLine('', undefined)).toBeNull();
  });
});
