// Calendar dates in the device's local timezone (never UTC): lending and the Added
// column both stamp the day the user sees, so evening actions stay on today's date.
/** YYYY-MM-DD in the device's local timezone, for a Date or ISO timestamp (default now). Never pass a date-only YYYY-MM-DD string: it parses as UTC midnight and shifts a day west of UTC. */
export function localDateString(value: Date | string = new Date()): string {
  const at = typeof value === 'string' ? new Date(value) : value;
  const month = String(at.getMonth() + 1).padStart(2, '0');
  const day = String(at.getDate()).padStart(2, '0');
  return `${at.getFullYear()}-${month}-${day}`;
}

/**
 * The device's offset from UTC in whole minutes at `at` (default now): -300 for US Central daylight time, +60 for Paris in winter.
 * The scan session carries it so the server can date what it adds for the session (a commit, an auto-add) by the day the person
 * is in; the server has no other way to know their time zone.
 */
export function deviceUtcOffsetMinutes(at: Date = new Date()): number {
  return Math.round(-at.getTimezoneOffset()) || 0; // `|| 0` turns -0 (UTC) into 0
}

/** A YYYY-MM-DD string as a local-midnight Date, or null when it is not a real calendar date. */
export function parseLocalDate(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return null;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return localDateString(date) === value ? date : null;
}
