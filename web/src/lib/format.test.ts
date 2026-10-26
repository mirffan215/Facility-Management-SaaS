import { describe, expect, it } from 'vitest';
import { formatBytes, formatDate, formatNumber, localeFor, localizedName } from './format';

describe('dates', () => {
  it('shows a date-only value as that calendar day whatever the time zone', () => {
    const original = process.env.TZ;
    for (const tz of ['Pacific/Kiritimati', 'America/Los_Angeles', 'UTC']) {
      process.env.TZ = tz;
      expect(formatDate('2026-10-25', 'en', 'gregorian')).toBe('25 Oct 2026');
    }
    process.env.TZ = original;
  });

  it('formats the Hijri calendar differently from the Gregorian one', () => {
    const greg = formatDate('2026-10-25', 'ar', 'gregorian');
    const hijri = formatDate('2026-10-25', 'ar', 'hijri');
    expect(hijri).not.toBe(greg);
    expect(hijri).toMatch(/1448/); // 25 Oct 2026 falls in Hijri year 1448
    expect(greg).toMatch(/2026/);
  });

  it('uses Latin digits in both languages', () => {
    expect(formatDate('2026-10-25', 'ar', 'gregorian')).toMatch(/[0-9]/);
    expect(formatDate('2026-10-25', 'ar', 'gregorian')).not.toMatch(/[٠-٩]/);
  });

  it('adds the time for instants only', () => {
    expect(formatDate('2026-10-25T10:30:00Z', 'en', 'gregorian', true)).toMatch(/\d{1,2}:\d{2}/);
    expect(formatDate('2026-10-25', 'en', 'gregorian', true)).not.toMatch(/:/);
  });

  it('returns empty or the original for missing or invalid input', () => {
    expect(formatDate(null, 'en', 'gregorian')).toBe('');
    expect(formatDate(undefined, 'en', 'gregorian')).toBe('');
    expect(formatDate('not a date', 'en', 'gregorian')).toBe('not a date');
  });

  it('builds locales with the calendar and numbering extensions', () => {
    expect(localeFor('ar', 'hijri')).toBe('ar-SA-u-ca-islamic-umalqura-nu-latn');
    expect(localeFor('en', 'gregorian')).toBe('en-GB-u-ca-gregory-nu-latn');
  });
});

describe('numbers and sizes', () => {
  it('groups thousands and keeps decimals', () => {
    expect(formatNumber(1200.5, 'en')).toBe('1,200.5');
    expect(formatNumber(null, 'en')).toBe('');
  });

  it('formats byte sizes', () => {
    expect(formatBytes(512, 'en')).toBe('512 B');
    expect(formatBytes(2048, 'en')).toBe('2 KB');
    expect(formatBytes(5 * 1024 * 1024, 'en')).toBe('5 MB');
  });
});

describe('localizedName', () => {
  const place = { name: 'Riyadh HQ', nameAr: 'المقر الرئيسي' };
  it('prefers Arabic in Arabic and falls back to English', () => {
    expect(localizedName(place, 'ar')).toBe('المقر الرئيسي');
    expect(localizedName(place, 'ar-SA')).toBe('المقر الرئيسي');
    expect(localizedName({ name: 'Only English', nameAr: null }, 'ar')).toBe('Only English');
    expect(localizedName(place, 'en')).toBe('Riyadh HQ');
  });
});
