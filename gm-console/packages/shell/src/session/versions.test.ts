import { describe, expect, it } from 'vitest';
import type { GmMe } from '../types';
import { compareFrameworkVersions, majorMinor, versionSkew } from './versions';

const me = (frameworkVersion: string, gameVersion = '1.0.0'): GmMe => ({
  staffId: 'admin',
  displayName: 'Admin',
  gameId: 'sample',
  environmentId: 'local',
  permissions: new Set(),
  frameworkVersion,
  gameVersion,
});

describe('version skew (FR-056)', () => {
  it.each([
    ['0.7.0', 'same', '0.7.0'],
    ['0.7.0+abc', 'same', '0.7.0'],
    ['0.7.3', 'same', '0.7.0'],
    ['0.8.1', 'minor-differs', '0.7.0'],
    ['1.7.0', 'minor-differs', '0.7.0'],
    ['0.7.0-beta.1', 'same', '0.7.0'],
    ['0.7', 'same', '0.7.0'],
    ['', 'unparsable', '0.7.0'],
    ['dev', 'unparsable', '0.7.0'],
    ['0.7.0', 'unparsable', 'unknown'],
  ])('server %j against a build for %3$s is %2$s', (server, expected, built) => {
    expect(compareFrameworkVersions(built, server)).toBe(expected);
  });

  it('parses major.minor before + or -', () => {
    expect(majorMinor('0.7.0+0123abcd')).toBe('0.7');
    expect(majorMinor('10.20.30-rc.1+sha')).toBe('10.20');
    expect(majorMinor('v0.7.0')).toBeUndefined();
  });

  it('reports a different game version only when the build carries one', () => {
    expect(versionSkew({ frameworkVersion: '0.7.0' }, me('0.7.0', '2.0.0')).gameDiffers).toBe(false);
    expect(versionSkew({ frameworkVersion: '0.7.0', gameVersion: '1.0.0' }, me('0.7.0', '2.0.0')).gameDiffers).toBe(true);
    expect(versionSkew({ frameworkVersion: '0.7.0', gameVersion: '2.0.0' }, me('0.7.0', '2.0.0')).gameDiffers).toBe(false);
  });
});
