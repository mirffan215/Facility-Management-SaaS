import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import ar from './ar.json';
import en from './en.json';

const flat = (o: Record<string, unknown>, prefix = ''): Record<string, string> =>
  Object.entries(o).reduce<Record<string, string>>((acc, [k, v]) => {
    if (typeof v === 'object' && v !== null) Object.assign(acc, flat(v as Record<string, unknown>, `${prefix}${k}.`));
    else acc[`${prefix}${k}`] = String(v);
    return acc;
  }, {});

const english = flat(en);
const arabic = flat(ar);

function sourceFiles(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) return e.name === 'test' ? [] : sourceFiles(full);
    return /\.(tsx?)$/.test(e.name) && !/\.test\./.test(e.name) ? [full] : [];
  });
}

describe('translations', () => {
  it('every key used in the code exists in English', () => {
    const used = new Set<string>();
    for (const file of sourceFiles(path.resolve(__dirname, '..'))) {
      const text = fs.readFileSync(file, 'utf8');
      for (const m of text.matchAll(/\bt\(\s*['"]([a-zA-Z0-9_.]+)['"]/g)) used.add(m[1]);
    }
    expect([...used].filter((k) => !(k in english)).sort()).toEqual([]);
  });

  it('keys built from enums are all translated (values come from the API types)', () => {
    const groups: Record<string, string[]> = {
      locationType: ['Site', 'Building', 'Floor', 'Space'],
      assetStatus: ['Planned', 'Active', 'UnderMaintenance', 'OutOfService', 'Retired'],
      criticality: ['Low', 'Medium', 'High', 'Critical'],
      warranty: ['None', 'Active', 'ExpiringSoon', 'Expired'],
      meterKind: ['Cumulative', 'Gauge'],
      readingSource: ['Manual', 'Mobile', 'Import', 'Device'],
      docKind: ['Manual', 'Warranty', 'Certificate', 'Photo', 'Drawing', 'Other'],
      fieldType: ['Text', 'Number', 'Date', 'Boolean', 'Choice'],
      fieldTarget: ['Asset', 'Location'],
      pmKind: ['Open', 'Projected', 'MeterEstimate'],
      pmBasis: ['Fixed', 'Floating', 'None'],
      pmUnit: ['Day', 'Week', 'Month', 'Year'],
      pmTarget: ['Asset', 'AssetClass'],
      'pm.basisHint': ['Fixed', 'Floating', 'None'],
      movementType: ['Receipt', 'Issue', 'Return', 'TransferOut', 'TransferIn', 'Adjustment', 'CountAdjustment'],
      takeStatus: ['Counting', 'Submitted', 'Approved', 'Cancelled'],
      'inventory.tab': ['stock', 'items', 'stores', 'takes'],
      'parts': ['reserve', 'issue', 'return', 'release'],
      'pm.tab': ['forecast', 'schedules', 'templates', 'compliance'],
    };
    for (const [group, values] of Object.entries(groups)) for (const v of values) {
      expect(english[`${group}.${v}`], `${group}.${v}`).toBeTruthy();
      expect(arabic[`${group}.${v}`], `ar ${group}.${v}`).toBeTruthy();
    }
  });

  it('Arabic has exactly the same keys as English', () => {
    expect(Object.keys(arabic).sort()).toEqual(Object.keys(english).sort());
  });

  it('Arabic uses the same placeholders as English and has no empty strings', () => {
    const placeholders = (s: string) => [...s.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1]).sort();
    for (const key of Object.keys(english)) {
      expect(placeholders(arabic[key]), key).toEqual(placeholders(english[key]));
      expect(arabic[key].trim(), key).not.toBe('');
      expect(english[key].trim(), key).not.toBe('');
    }
  });
});
