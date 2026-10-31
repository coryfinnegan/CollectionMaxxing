import { deviceUtcOffsetMinutes, localDateString, parseLocalDate } from './dates';

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

describe('deviceUtcOffsetMinutes', () => {
  it('is minus 300 in US Central daylight time, the offset the server dates a scan commit by', () => {
    expect(deviceUtcOffsetMinutes(new Date(EVENING))).toBe(-300);
  });

  it('is minus 360 in US Central standard time', () => {
    expect(deviceUtcOffsetMinutes(new Date('2026-01-15T18:00:00Z'))).toBe(-360);
  });

  it('is the device offset right now by default', () => {
    expect(deviceUtcOffsetMinutes()).toBe(-new Date().getTimezoneOffset() || 0);
  });

  it('is a whole number of minutes with no negative zero, for a server that wants an integer', () => {
    const offset = deviceUtcOffsetMinutes(new Date(EVENING));
    expect(Number.isInteger(offset)).toBe(true);
    expect(Object.is(deviceUtcOffsetMinutes(), -0)).toBe(false);
  });
});

describe('parseLocalDate', () => {
  it('parses a date-only string at local midnight, with no UTC shift', () => {
    const date = parseLocalDate('2026-10-24');
    expect(date?.getFullYear()).toBe(2026);
    expect(date?.getMonth()).toBe(9);
    expect(date?.getDate()).toBe(24);
    expect(localDateString(date!)).toBe('2026-10-24');
  });

  it('returns null for anything that is not a real calendar date', () => {
    expect(parseLocalDate('')).toBeNull();
    expect(parseLocalDate('June 2')).toBeNull();
    expect(parseLocalDate('2024-13-02')).toBeNull();
    expect(parseLocalDate('2024-02-30')).toBeNull();
  });
});
