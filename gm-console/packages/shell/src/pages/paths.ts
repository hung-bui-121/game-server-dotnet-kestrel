/** Console hash routes with a parameter. Values are encoded into one route segment (router paths, never API paths). */

export function playerPath(playerId: string): string {
  return `/players/${encodeURIComponent(playerId)}`;
}

export function receiptPath(receiptId: string): string {
  return `/receipts/${encodeURIComponent(receiptId)}`;
}

export function staffPath(staffId: string): string {
  return `/staff/${encodeURIComponent(staffId)}`;
}

/** HTTP status of a missing resource. */
export const NOT_FOUND = 404;
