export type Calendar = 'gregorian' | 'hijri';

/** BCP 47 locale for a language and calendar. Latin digits are used in both languages so numbers read the same everywhere. */
export function localeFor(lang: string, calendar: Calendar): string {
  const base = lang.startsWith('ar') ? 'ar-SA' : 'en-GB';
  return `${base}-u-ca-${calendar === 'hijri' ? 'islamic-umalqura' : 'gregory'}-nu-latn`;
}

/**
 * Formats a date or instant. A date-only value ("2026-10-25") is a calendar day, so it is shown as that day in every time zone; an
 * instant is shown in the viewer's time zone.
 */
export function formatDate(value: string | null | undefined, lang: string, calendar: Calendar, withTime = false): string {
  if (!value) return '';
  const dateOnly = /^\d{4}-\d{2}-\d{2}$/.test(value);
  const date = new Date(dateOnly ? `${value}T00:00:00Z` : value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(localeFor(lang, calendar), {
    dateStyle: 'medium',
    ...(withTime && !dateOnly ? { timeStyle: 'short' } : {}),
    ...(dateOnly ? { timeZone: 'UTC' } : {}),
  }).format(date);
}

export function formatNumber(value: number | null | undefined, lang: string, maximumFractionDigits = 4): string {
  if (value === null || value === undefined) return '';
  return new Intl.NumberFormat(localeFor(lang, 'gregorian'), { maximumFractionDigits }).format(value);
}

export function formatBytes(bytes: number, lang: string): string {
  if (bytes < 1024) return `${formatNumber(bytes, lang)} B`;
  if (bytes < 1024 * 1024) return `${formatNumber(bytes / 1024, lang, 1)} KB`;
  return `${formatNumber(bytes / (1024 * 1024), lang, 1)} MB`;
}

/** The name to show: Arabic when the UI is Arabic and one exists, otherwise the English (primary) name. */
export function localizedName(entity: { name: string; nameAr: string | null }, lang: string): string {
  return lang.startsWith('ar') && entity.nameAr ? entity.nameAr : entity.name;
}
