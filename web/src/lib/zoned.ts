/** The offset of a time zone from UTC at a moment, in minutes (east is positive). Uses the browser's time zone data. */
export function zoneOffsetMinutes(zone: string, at: Date): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: zone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(at);
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'), get('second'));
  return Math.round((asUtc - Math.floor(at.getTime() / 1000) * 1000) / 60000);
}

/** The UTC moment at which a wall-clock date and time ("2027-03-02", "09:30") occurs in a time zone. */
export function zonedTimeToUtc(date: string, time: string, zone: string): Date {
  const [y, mo, d] = date.split('-').map(Number);
  const [h, mi] = time.split(':').map(Number);
  const wall = Date.UTC(y, mo - 1, d, h, mi);
  // The offset depends on the answer, so settle it in two steps (enough except in the hour a clock change skips or repeats).
  let guess = new Date(wall - zoneOffsetMinutes(zone, new Date(wall)) * 60000);
  guess = new Date(wall - zoneOffsetMinutes(zone, guess) * 60000);
  return guess;
}

/** A moment shown as a wall-clock time in a time zone ("09:30"), in the reader's language. */
export function formatTimeInZone(value: string | Date, zone: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, { timeStyle: 'short', timeZone: zone }).format(new Date(value));
}

/** A moment shown as a date and time in a time zone. */
export function formatDateTimeInZone(value: string | Date, zone: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, { dateStyle: 'medium', timeStyle: 'short', timeZone: zone }).format(new Date(value));
}

/** The calendar day ("2027-03-02") of a moment in a time zone. */
export function dayInZone(value: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(value);
  return `${parts.find((p) => p.type === 'year')?.value}-${parts.find((p) => p.type === 'month')?.value}-${parts.find((p) => p.type === 'day')?.value}`;
}
