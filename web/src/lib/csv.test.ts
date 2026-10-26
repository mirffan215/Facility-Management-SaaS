import { describe, expect, it } from 'vitest';
import { ASSET_TEMPLATE, LOCATION_TEMPLATE } from './csv';

const header = (csv: string) => csv.split('\n')[0].split(',');
const rows = (csv: string) => csv.trim().split('\n').slice(1);

describe('import templates', () => {
  it('use exactly the columns the server accepts', () => {
    expect(header(LOCATION_TEMPLATE)).toEqual(['type', 'code', 'parentCode', 'name', 'nameAr', 'address', 'timeZone', 'areaSquareMetres']);
    expect(header(ASSET_TEMPLATE)).toEqual(['code', 'name', 'nameAr', 'classCode', 'locationCode', 'parentAssetCode', 'criticality', 'status', 'manufacturer', 'model', 'serialNumber', 'installDate', 'warrantyExpiry']);
  });

  it('have rows as wide as the header and parents before children', () => {
    for (const t of [LOCATION_TEMPLATE, ASSET_TEMPLATE]) for (const r of rows(t)) expect(r.split(',').length).toBe(header(t).length);
    const codes = rows(LOCATION_TEMPLATE).map((r) => r.split(','));
    const seen = new Set<string>();
    for (const [, code, parent] of codes) {
      if (parent) expect(seen.has(parent)).toBe(true);
      seen.add(code);
    }
  });
});
