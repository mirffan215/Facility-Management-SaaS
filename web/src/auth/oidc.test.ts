import { describe, expect, it } from 'vitest';
import { safeReturnPath } from './oidc';

describe('safeReturnPath', () => {
  it('keeps same-site paths', () => {
    expect(safeReturnPath('/assets?status=Active')).toBe('/assets?status=Active');
    expect(safeReturnPath('/')).toBe('/');
  });

  it('refuses anything that could leave the site', () => {
    for (const bad of ['https://evil.test/', '//evil.test/', '/\\evil.test', 'javascript:alert(1)', 'assets', '', null, undefined]) {
      expect(safeReturnPath(bad as string | null | undefined)).toBe('/');
    }
  });
});
