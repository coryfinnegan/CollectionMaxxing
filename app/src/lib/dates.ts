// Calendar dates in the device's local timezone (never UTC): lending and the Added
// column both stamp the day the user sees, so evening actions stay on today's date.
/** YYYY-MM-DD in the device's local timezone, for a Date or ISO timestamp (default now). */
export function localDateString(value: Date | string = new Date()): string {
  const at = typeof value === 'string' ? new Date(value) : value;
  const month = String(at.getMonth() + 1).padStart(2, '0');
  const day = String(at.getDate()).padStart(2, '0');
  return `${at.getFullYear()}-${month}-${day}`;
}
