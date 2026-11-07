import type { GmBuiltFor, GmMe } from '../types';

/** `same`: equal `major.minor`; `minor-differs`: a different one; `unparsable`: either side has no `major.minor` (FR-056). */
export type FrameworkSkew = 'same' | 'minor-differs' | 'unparsable';

export interface VersionSkew {
  framework: FrameworkSkew;
  builtFor: GmBuiltFor;
  serverFrameworkVersion: string;
  serverGameVersion: string;
  /** The build carries a game version and the server's differs (an informational footer line). */
  gameDiffers: boolean;
}

const MAJOR_MINOR = /^(\d+)\.(\d+)(?:\.\d+)?$/;

/** `major.minor` of the text before the first `+` or `-` (`0.7.0+abc` → 0.7), or undefined. */
export function majorMinor(version: string): string | undefined {
  const core = version.trim().split(/[+-]/, 1)[0] ?? '';
  const match = MAJOR_MINOR.exec(core);
  return match ? `${Number(match[1])}.${Number(match[2])}` : undefined;
}

export function compareFrameworkVersions(builtFor: string, server: string): FrameworkSkew {
  const built = majorMinor(builtFor);
  const actual = majorMinor(server);
  if (built === undefined || actual === undefined) {
    return 'unparsable';
  }

  return built === actual ? 'same' : 'minor-differs';
}

/** Version comparison of this build with `/me`; nothing here blocks (Q19). */
export function versionSkew(builtFor: GmBuiltFor, me: GmMe): VersionSkew {
  return {
    framework: compareFrameworkVersions(builtFor.frameworkVersion, me.frameworkVersion),
    builtFor,
    serverFrameworkVersion: me.frameworkVersion,
    serverGameVersion: me.gameVersion,
    gameDiffers: builtFor.gameVersion !== undefined && builtFor.gameVersion !== me.gameVersion,
  };
}
