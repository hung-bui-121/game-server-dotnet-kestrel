import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import * as shell from '../index';
import { parseCatalog } from '../catalog/parseCatalog';
import { toMe } from '../session/phases';
import { catalogDocument, meBody, READ_ROUTES } from '../test/fixtures';
import { renderAt } from '../test/render';
import { builtinRegistry } from './builtins';
import { Menu } from './Menu';
import { createRegistry, isVisible, menuGroups } from './registry';

const registry = builtinRegistry();
const me = (permissions: string[]) => toMe(meBody({ permissions }));
const catalogWithout = (...missing: string[]) =>
  parseCatalog(catalogDocument(Object.fromEntries(Object.entries(READ_ROUTES).filter(([name]) => !missing.includes(name)))));
const full = catalogWithout();
const menuTitles = (permissions: string[], catalog = full) =>
  menuGroups(registry.pages, me(permissions), catalog).flatMap((g) => g.pages.map((p) => `${g.group}/${p.title}`));

describe('navigation (FR-066, FR-067, US7-1)', () => {
  it('lists Players, Staff and Operations for a staff member holding the three permissions', () => {
    expect(menuTitles(['gm.access', 'player.read', 'staff.read', 'ops.read'])).toEqual(['Players/Players', 'Staff/Staff', 'Operations/Operations']);
  });

  it('shows a player.read-only staff member Players only', () => {
    expect(menuTitles(['gm.access', 'player.read'])).toEqual(['Players/Players']);
  });

  it('hides a page whose route the catalog does not serve', () => {
    expect(menuTitles(['player.read', 'staff.read'], catalogWithout('gm.staff_list'))).toEqual(['Players/Players']);
  });

  it('hides the Ledger tab without economy.ledger in the catalog and keeps the other tabs', () => {
    const visibleTabs = (catalog = full) => registry.playerTabs.filter((t) => isVisible(t, me(['player.read']), catalog)).map((t) => t.id);

    expect(visibleTabs()).toEqual(['receipts', 'audit', 'ledger']);
    expect(visibleTabs(catalogWithout('economy.ledger'))).toEqual(['receipts', 'audit']);
    expect(visibleTabs(catalogWithout('gm.player_audit'))).toEqual(['receipts', 'ledger']);
  });

  it('shows Operations when at least one card route is served', () => {
    const ops = registry.pages.find((p) => p.id === 'ops')!;
    const staff = me(['ops.read']);

    expect(isVisible(ops, staff, catalogWithout('gm.health', 'gm.ops_gate'))).toBe(true);
    expect(isVisible(ops, staff, catalogWithout('gm.health', 'gm.ops_gate', 'gm.ops_version'))).toBe(false);
    expect(isVisible(ops, me(['staff.read']), full)).toBe(false);
  });

  it('refuses a duplicate page or tab id', () => {
    const r = createRegistry();
    const page = registry.pages[0]!;
    r.addPage(page);
    expect(() => r.addPage(page)).toThrow(/registered twice/);
    const tab = registry.playerTabs[0]!;
    r.addPlayerTab(tab);
    expect(() => r.addPlayerTab(tab)).toThrow(/registered twice/);
  });

  it('renders the menu as grouped links to hash routes', () => {
    renderAt(<Menu groups={menuGroups(registry.pages, me(['player.read', 'ops.read']), full)} />);

    const players = screen.getByRole('group', { name: 'Players' });
    expect(within(players).getByRole('link', { name: 'Players' })).toHaveAttribute('href', '/players');
    expect(within(screen.getByRole('group', { name: 'Operations' })).getByRole('link', { name: 'Operations' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Staff' })).toBeNull();
  });
});

describe('public surface (contract public-api §6, D9)', () => {
  it('exports only createGmConsole at run time: the registry stays internal in GM-2', () => {
    expect(Object.keys(shell).sort()).toEqual(['createGmConsole']);
  });
});
