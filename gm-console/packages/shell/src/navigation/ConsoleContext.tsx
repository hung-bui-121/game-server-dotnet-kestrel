import { createContext, useContext } from 'react';
import type { GmClient } from '../api/client';
import type { GmCatalog } from '../catalog/parseCatalog';
import type { GmConsoleConfig, GmMe } from '../types';
import type { RegistryContents } from './registry';

/** What a ready console gives its pages: the API, the staff member, the catalog and the registry. */
export interface ConsoleValue {
  config: GmConsoleConfig;
  api: GmClient;
  me: GmMe;
  catalog: GmCatalog;
  registry: RegistryContents;
}

export const ConsoleContext = createContext<ConsoleValue | undefined>(undefined);

export function useConsole(): ConsoleValue {
  const value = useContext(ConsoleContext);
  if (!value) {
    throw new Error('useConsole must be used inside a ready console.');
  }

  return value;
}
