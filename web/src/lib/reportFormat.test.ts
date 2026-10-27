import { describe, expect, it } from 'vitest';
import { barWidths, formatKpi, isWeekly, keyId, pointLabelGroup } from './reportFormat';

describe('report formatting', () => {
  it('shows a dash for nothing to measure and units for the rest', () => {
    expect(formatKpi(null, 'percent', 'en')).toBe('—');
    expect(formatKpi(66.7, 'percent', 'en')).toBe('66.7%');
    expect(formatKpi(12.5, 'hours', 'en')).toBe('12.5 h');
    expect(formatKpi(1234, 'count', 'en')).toBe('1,234');
    expect(formatKpi(500, 'area', 'en')).toBe('500 m²');
    expect(formatKpi(0, 'percent', 'en')).toBe('0%'); // a real zero is shown, unlike "no data"
  });

  it('sizes bars against the largest value and copes with all zeros', () => {
    expect(barWidths([5, 10, 0])).toEqual([50, 100, 0]);
    expect(barWidths([0, 0])).toEqual([0, 0]);
    expect(barWidths([])).toEqual([]);
  });

  it('knows which charts have category labels to translate and which are weeks', () => {
    expect(pointLabelGroup('work.openByStatus')).toBe('workStatus');
    expect(pointLabelGroup('bookings.hoursByResource')).toBeNull();
    expect(isWeekly('work.createdByWeek')).toBe(true);
    expect(isWeekly('work.openByStatus')).toBe(false);
    expect(keyId('work.created')).toBe('work_created');
  });
});
