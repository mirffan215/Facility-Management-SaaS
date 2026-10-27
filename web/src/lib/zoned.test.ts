import { describe, expect, it } from 'vitest';
import { dayInZone, formatTimeInZone, zoneOffsetMinutes, zonedTimeToUtc } from './zoned';

describe('time zones', () => {
  it('knows a zone offset at a moment, including a clock change', () => {
    expect(zoneOffsetMinutes('Asia/Riyadh', new Date('2027-03-02T00:00:00Z'))).toBe(180);
    expect(zoneOffsetMinutes('America/New_York', new Date('2027-03-10T12:00:00Z'))).toBe(-300);
    expect(zoneOffsetMinutes('America/New_York', new Date('2027-03-20T12:00:00Z'))).toBe(-240);
    expect(zoneOffsetMinutes('UTC', new Date('2027-03-02T00:00:00Z'))).toBe(0);
  });

  it('turns a wall-clock time in a zone into the right UTC moment', () => {
    expect(zonedTimeToUtc('2027-03-02', '09:00', 'Asia/Riyadh').toISOString()).toBe('2027-03-02T06:00:00.000Z');
    expect(zonedTimeToUtc('2027-03-02', '09:00', 'UTC').toISOString()).toBe('2027-03-02T09:00:00.000Z');
    expect(zonedTimeToUtc('2027-03-10', '10:00', 'America/New_York').toISOString()).toBe('2027-03-10T15:00:00.000Z');
    expect(zonedTimeToUtc('2027-03-20', '10:00', 'America/New_York').toISOString()).toBe('2027-03-20T14:00:00.000Z');
  });

  it('shows a moment as the wall-clock time and day in a zone', () => {
    expect(formatTimeInZone('2027-03-02T06:00:00Z', 'Asia/Riyadh', 'en-GB')).toBe('09:00');
    expect(dayInZone(new Date('2027-03-02T22:00:00Z'), 'Asia/Riyadh')).toBe('2027-03-03');
    expect(dayInZone(new Date('2027-03-02T22:00:00Z'), 'UTC')).toBe('2027-03-02');
  });
});
