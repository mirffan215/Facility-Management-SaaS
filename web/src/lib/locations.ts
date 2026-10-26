import type { LocationNode, LocationType } from '../api/types';

/** Mirrors the server's hierarchy rules (the server remains the authority). */
export function allowedChildTypes(parent: LocationType | null): LocationType[] {
  switch (parent) {
    case null:
      return ['Site'];
    case 'Site':
      return ['Building'];
    case 'Building':
      return ['Floor', 'Space'];
    case 'Floor':
      return ['Space'];
    default:
      return [];
  }
}

export function allowedParentTypes(type: LocationType): LocationType[] {
  switch (type) {
    case 'Building':
      return ['Site'];
    case 'Floor':
      return ['Building'];
    case 'Space':
      return ['Floor', 'Building'];
    default:
      return [];
  }
}

export interface TreeItem extends LocationNode {
  children: TreeItem[];
  depth: number;
}

export function buildTree(nodes: LocationNode[]): TreeItem[] {
  const byId = new Map<string, TreeItem>(nodes.map((n) => [n.id, { ...n, children: [], depth: 0 }]));
  const roots: TreeItem[] = [];
  for (const item of byId.values()) {
    const parent = item.parentId ? byId.get(item.parentId) : undefined;
    (parent ? parent.children : roots).push(item);
  }

  const order = (items: TreeItem[], depth: number) => {
    items.sort((a, b) => a.code.localeCompare(b.code));
    for (const item of items) {
      item.depth = depth;
      order(item.children, depth + 1);
    }
  };
  order(roots, 0);
  return roots;
}

export function flatten(items: TreeItem[]): TreeItem[] {
  return items.flatMap((i) => [i, ...flatten(i.children)]);
}

/** Ids of a node and everything beneath it, used to stop a move into its own subtree. */
export function subtreeIds(items: TreeItem[], id: string): Set<string> {
  const found = flatten(items).find((i) => i.id === id);
  return new Set(found ? flatten([found]).map((i) => i.id) : []);
}

// Unicode isolates: each name is laid out on its own, so an English name between Arabic ones (or the reverse) cannot reorder its neighbours.
const FSI = '\u2068';
const PDI = '\u2069';

/** "Site › Building › Floor" for a location id, safe to show next to text of the other direction. Pass isolate=false for plain text. */
export function breadcrumb(nodes: LocationNode[], id: string, nameOf: (n: LocationNode) => string, isolate = true): string {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const parts: string[] = [];
  for (let cur = byId.get(id); cur; cur = cur.parentId ? byId.get(cur.parentId) : undefined) {
    const name = nameOf(cur);
    parts.unshift(isolate ? `${FSI}${name}${PDI}` : name);
  }

  return parts.join(' › ');
}
