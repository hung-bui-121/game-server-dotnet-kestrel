import type { ComponentType } from 'react';
import type { GmClient } from '../api/client';
import type { GmCatalog } from '../catalog/parseCatalog';
import type { GmMe } from '../types';

/**
 * Internal page registry (FR-067, design §7.3 shapes). Built-in pages register through it like a game extension will in GM-4;
 * it is not exported from the package in GM-2.
 */

export interface GmPageProps {
  api: GmClient;
  me: GmMe;
  catalog: GmCatalog;
}

export interface GmPlayerTabProps extends GmPageProps {
  playerId: string;
}

export interface GmPageDef {
  id: string;
  title: string;
  /** Menu group; pages whose path has a parameter are not in the menu. */
  group: string;
  /** Hash route, e.g. `/players` or `/players/:playerId`. */
  path: string;
  /** UX only: the API still checks every call. */
  requiredPermission: string;
  /** Hidden when one of these routes is missing from the runtime catalog. */
  requiredRoutes?: string[];
  /** Internal (not in design §7.3): hidden unless at least one of these routes is in the catalog (Ops cards). */
  anyOfRoutes?: string[];
  component: ComponentType<GmPageProps>;
  order?: number;
}

export interface GmPlayerTabDef extends Omit<GmPageDef, 'path' | 'group' | 'component' | 'anyOfRoutes'> {
  component: ComponentType<GmPlayerTabProps>;
}

export interface GmConsoleRegistry {
  addPage(page: GmPageDef): void;
  addPlayerTab(tab: GmPlayerTabDef): void;
}

export interface RegistryContents {
  pages: readonly GmPageDef[];
  playerTabs: readonly GmPlayerTabDef[];
}

/** Default order of a definition without one. */
export const DEFAULT_ORDER = 1000;

const byOrder = <T extends { order?: number; title: string }>(a: T, b: T) =>
  (a.order ?? DEFAULT_ORDER) - (b.order ?? DEFAULT_ORDER) || a.title.localeCompare(b.title);

/** A registry collecting definitions; ids are unique per kind. */
export function createRegistry(): GmConsoleRegistry & { contents(): RegistryContents } {
  const pages: GmPageDef[] = [];
  const playerTabs: GmPlayerTabDef[] = [];
  return {
    addPage(page) {
      if (pages.some((p) => p.id === page.id)) {
        throw new Error(`GM console page '${page.id}' is registered twice.`);
      }

      pages.push(page);
    },
    addPlayerTab(tab) {
      if (playerTabs.some((t) => t.id === tab.id)) {
        throw new Error(`GM console player tab '${tab.id}' is registered twice.`);
      }

      playerTabs.push(tab);
    },
    contents: () => ({ pages: [...pages].sort(byOrder), playerTabs: [...playerTabs].sort(byOrder) }),
  };
}

/**
 * A page or tab is shown exactly when the staff member holds its permission and the catalog serves every route it needs
 * (FR-066); `anyOfRoutes` needs at least one.
 */
export function isVisible(
  def: Pick<GmPageDef, 'requiredPermission' | 'requiredRoutes' | 'anyOfRoutes'>,
  me: GmMe,
  catalog: GmCatalog,
): boolean {
  return (
    me.permissions.has(def.requiredPermission) &&
    (def.requiredRoutes ?? []).every((route) => catalog.operations.has(route)) &&
    (def.anyOfRoutes === undefined || def.anyOfRoutes.some((route) => catalog.operations.has(route)))
  );
}

/** Whether a catalog serves a route (cards and search modes inside a page). */
export function hasRoute(catalog: GmCatalog, routeName: string): boolean {
  return catalog.operations.has(routeName);
}

export interface MenuGroup {
  group: string;
  pages: GmPageDef[];
}

/** Visible menu pages (no path parameter), grouped in order of their first page. */
export function menuGroups(pages: readonly GmPageDef[], me: GmMe, catalog: GmCatalog): MenuGroup[] {
  const groups: MenuGroup[] = [];
  for (const page of pages) {
    if (page.path.includes(':') || !isVisible(page, me, catalog)) {
      continue;
    }

    const existing = groups.find((g) => g.group === page.group);
    if (existing) {
      existing.pages.push(page);
    } else {
      groups.push({ group: page.group, pages: [page] });
    }
  }

  return groups;
}
