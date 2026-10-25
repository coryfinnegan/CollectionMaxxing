import { localDateString } from './dates';

// Timezone-sensitive: the gate runs this file with TZ=America/Chicago (see
// scripts/check.mjs). Setting process.env.TZ in-test is too late, so bare
// `jest` on a non-Central machine fails these tests by design.

// 9:30pm on Oct 24 in US Central, already Oct 25 in UTC.
const EVENING = '2026-10-25T02:30:00Z';

describe('localDateString', () => {
  it('stamps an evening moment with the local day, not tomorrow in UTC', () => {
    expect(localDateString(new Date(EVENING))).toBe('2026-10-24');
  });

  it('reads the local day off an ISO timestamp', () => {
    expect(localDateString(EVENING)).toBe('2026-10-24');
  });

  it('zero-pads month and day', () => {
    expect(localDateString(new Date('2026-09-01T12:00:00Z'))).toBe('2026-09-01');
  });
});
