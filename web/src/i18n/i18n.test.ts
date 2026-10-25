import { describe, expect, it } from 'vitest';
import ar from './ar.json';
import en from './en.json';
import { directionOf } from './index';

const keys = (o: Record<string, unknown>, prefix = ''): string[] =>
  Object.entries(o).flatMap(([k, v]) =>
    typeof v === 'object' && v !== null ? keys(v as Record<string, unknown>, `${prefix}${k}.`) : [`${prefix}${k}`],
  );

describe('i18n', () => {
  it('has the same keys in every language', () => {
    expect(keys(ar).sort()).toEqual(keys(en).sort());
  });

  it('uses RTL for Arabic only', () => {
    expect(directionOf('ar')).toBe('rtl');
    expect(directionOf('ar-SA')).toBe('rtl');
    expect(directionOf('en')).toBe('ltr');
  });
});
