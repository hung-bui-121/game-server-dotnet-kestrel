/** Public types of the shell (contract public-api §6). */

/** `config.json` of the static host (data-model §5). */
export interface GmConsoleConfig {
  apiBaseUrl: string;
  gameId: string;
  environmentId: string;
}

/** What the console was built for (FR-056). */
export interface GmBuiltFor {
  frameworkVersion: string;
  gameVersion?: string;
}

/** The signed-in staff member (`GET /gm/v1/me`). */
export interface GmMe {
  staffId: string;
  displayName: string;
  gameId: string;
  environmentId: string;
  permissions: ReadonlySet<string>;
  frameworkVersion: string;
  gameVersion: string;
}

/** An error answer (Problem Details) or a failure without a readable answer (`status` 0). */
export interface GmProblem {
  status: number;
  code: string;
  codeNumber?: number;
  title?: string;
  detail?: string;
  retryable: boolean;
  errors: Record<string, string[]>;
  requestId?: string;
}
