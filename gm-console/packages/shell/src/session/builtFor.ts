import type { GmBuiltFor } from '../types';

declare global {
  /** Build stamp (slice 018 FR-056, data-model §6): defined by the app's Vite config (`define`); tests define a default. */
  const __GM_BUILT_FOR__: GmBuiltFor | undefined;
}

/** Framework version used when an app build does not define the stamp: never parses, so the console warns (FR-056). */
export const UNKNOWN_FRAMEWORK_VERSION = 'unknown';

/** The stamp of this build; an app that does not define it gets the "unknown" version and a skew warning. */
export function readBuiltFor(): GmBuiltFor {
  return typeof __GM_BUILT_FOR__ === 'undefined' ? { frameworkVersion: UNKNOWN_FRAMEWORK_VERSION } : __GM_BUILT_FOR__;
}
