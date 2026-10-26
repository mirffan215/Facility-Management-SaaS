import { describe, expect, it } from 'vitest';
import type { LocationNode } from '../api/types';
import { allowedChildTypes, allowedParentTypes, breadcrumb, buildTree, flatten, subtreeIds } from './locations';

const node = (id: string, parentId: string | null, type: LocationNode['type'], code: string): LocationNode => ({ id, parentId, type, code, name: `Name ${code}`, nameAr: null });
const nodes = [
  node('f', 'b', 'Floor', 'F1'), node('s', null, 'Site', 'S1'), node('b', 's', 'Building', 'B1'), node('r', 'f', 'Space', 'R1'),
  node('b2', 's', 'Building', 'A0'), node('s2', null, 'Site', 'S0'),
];

describe('hierarchy rules', () => {
  it('mirror the server', () => {
    expect(allowedChildTypes(null)).toEqual(['Site']);
    expect(allowedChildTypes('Site')).toEqual(['Building']);
    expect(allowedChildTypes('Building')).toEqual(['Floor', 'Space']);
    expect(allowedChildTypes('Floor')).toEqual(['Space']);
    expect(allowedChildTypes('Space')).toEqual([]);
    expect(allowedParentTypes('Site')).toEqual([]);
    expect(allowedParentTypes('Space')).toEqual(['Floor', 'Building']);
  });

  it('are consistent: every allowed child lists the parent as allowed', () => {
    for (const parent of ['Site', 'Building', 'Floor', 'Space'] as const) {
      for (const child of allowedChildTypes(parent)) expect(allowedParentTypes(child)).toContain(parent);
    }
  });
});

describe('tree', () => {
  const roots = buildTree(nodes);

  it('nests nodes, orders siblings by code and records depth', () => {
    expect(roots.map((r) => r.code)).toEqual(['S0', 'S1']);
    const s1 = roots[1];
    expect(s1.children.map((c) => c.code)).toEqual(['A0', 'B1']);
    expect(flatten(roots).map((n) => [n.code, n.depth])).toContainEqual(['R1', 3]);
  });

  it('finds a subtree so a node cannot be moved into itself', () => {
    expect([...subtreeIds(roots, 'b')].sort()).toEqual(['b', 'f', 'r']);
    expect(subtreeIds(roots, 'nope').size).toBe(0);
  });

  it('builds a breadcrumb from the root down', () => {
    expect(breadcrumb(nodes, 'r', (n) => n.code, false)).toBe('S1 › B1 › F1 › R1');
    expect(breadcrumb(nodes, 'unknown', (n) => n.code)).toBe('');
  });

  it('isolates each name so mixed Arabic and English names keep their order', () => {
    const text = breadcrumb(nodes, 'b', (n) => n.code);
    expect(text).toBe('\u2068S1\u2069 › \u2068B1\u2069');
    expect(text.replace(/[\u2068\u2069]/g, '')).toBe('S1 › B1');
  });

  it('treats a node whose parent is missing as a root', () => {
    expect(buildTree([node('x', 'gone', 'Building', 'X')]).map((n) => n.code)).toEqual(['X']);
  });
});
