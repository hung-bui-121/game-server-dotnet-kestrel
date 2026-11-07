import { OpsPage } from '../pages/OpsPage';
import { LedgerTab } from '../pages/player/LedgerTab';
import { PlayerAuditTab } from '../pages/player/PlayerAuditTab';
import { ReceiptsTab } from '../pages/player/ReceiptsTab';
import { PlayerPage } from '../pages/PlayerPage';
import { PlayersPage } from '../pages/PlayersPage';
import { ReceiptPage } from '../pages/ReceiptPage';
import { StaffListPage } from '../pages/StaffListPage';
import { StaffPage } from '../pages/StaffPage';
import { createRegistry, type GmConsoleRegistry, type RegistryContents } from './registry';

/** Permissions of the built-in read pages (data-model §9). */
export const PLAYER_READ = 'player.read';
export const STAFF_READ = 'staff.read';
export const OPS_READ = 'ops.read';

/** The framework's read pages and player tabs (data-model §9), registered like a game extension will be (GM-4). */
export function registerBuiltins(registry: GmConsoleRegistry): void {
  registry.addPage({ id: 'players', title: 'Players', group: 'Players', path: '/players', requiredPermission: PLAYER_READ, requiredRoutes: ['gm.player'], component: PlayersPage, order: 10 });
  registry.addPage({ id: 'player', title: 'Player', group: 'Players', path: '/players/:playerId', requiredPermission: PLAYER_READ, requiredRoutes: ['gm.player'], component: PlayerPage, order: 11 });
  registry.addPage({ id: 'receipt', title: 'Receipt', group: 'Players', path: '/receipts/:receiptId', requiredPermission: PLAYER_READ, requiredRoutes: ['gm.receipt'], component: ReceiptPage, order: 12 });
  registry.addPage({ id: 'staff', title: 'Staff', group: 'Staff', path: '/staff', requiredPermission: STAFF_READ, requiredRoutes: ['gm.staff_list'], component: StaffListPage, order: 20 });
  registry.addPage({ id: 'staff-detail', title: 'Staff member', group: 'Staff', path: '/staff/:staffId', requiredPermission: STAFF_READ, requiredRoutes: ['gm.staff'], component: StaffPage, order: 21 });
  registry.addPage({ id: 'ops', title: 'Operations', group: 'Operations', path: '/ops', requiredPermission: OPS_READ, anyOfRoutes: ['gm.health', 'gm.ops_version', 'gm.ops_gate'], component: OpsPage, order: 30 });

  registry.addPlayerTab({ id: 'receipts', title: 'Receipts', requiredPermission: PLAYER_READ, requiredRoutes: ['gm.player_receipts'], component: ReceiptsTab, order: 10 });
  registry.addPlayerTab({ id: 'audit', title: 'Audit', requiredPermission: PLAYER_READ, requiredRoutes: ['gm.player_audit'], component: PlayerAuditTab, order: 20 });
  registry.addPlayerTab({ id: 'ledger', title: 'Ledger', requiredPermission: PLAYER_READ, requiredRoutes: ['economy.ledger'], component: LedgerTab, order: 30 });
}

/** The registry of the default console. */
export function builtinRegistry(): RegistryContents {
  const registry = createRegistry();
  registerBuiltins(registry);
  return registry.contents();
}
