import type { Kpi } from '../api/types';
import { formatNumber } from './format';

/** A figure for a tile or a chart: a dash when there is nothing to measure (never a zero that looks like a result). */
export function formatKpi(value: number | null, unit: Kpi['unit'], lang: string): string {
  if (value === null) return '—';
  const n = formatNumber(value, lang, unit === 'count' || unit === 'area' ? 0 : unit === 'money' ? 2 : 1);
  switch (unit) {
    case 'percent':
      return `${n}%`;
    case 'hours':
      return `${n} h`;
    case 'minutes':
      return `${n} min`;
    case 'area':
      return `${n} m²`;
    default:
      return n;
  }
}

/** Where a chart label's translation lives, if it is a category rather than a date or a name. */
export function pointLabelGroup(seriesKey: string): string | null {
  if (seriesKey === 'work.openByStatus') return 'workStatus';
  if (seriesKey === 'work.openByPriority') return 'workPriority';
  if (seriesKey === 'work.costByKind') return 'reports.costKind';
  if (seriesKey === 'facilities.assetsByStatus') return 'assetStatus';
  if (seriesKey === 'vendors.byStatus') return 'vendorStatus';
  if (seriesKey === 'spaces.utilisationByKind') return 'spaceKind';
  if (seriesKey === 'visitors.byKind') return 'visitKind';
  return null;
}

export const isWeekly = (seriesKey: string) => /By(Week)$/.test(seriesKey);

/** The id of a translation key for a figure ("work.created" -> "work_created"): dots would be read as nesting. */
export const keyId = (key: string) => key.replace('.', '_');

/** Share of the largest value, for the width of a bar. */
export function barWidths(values: number[]): number[] {
  const max = Math.max(0, ...values.map((v) => Math.abs(v)));
  return values.map((v) => (max === 0 ? 0 : Math.round((Math.abs(v) / max) * 100)));
}
