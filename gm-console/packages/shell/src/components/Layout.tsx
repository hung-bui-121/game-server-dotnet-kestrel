import { Button, Group, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import type { VersionSkew } from '../session/versions';
import type { GmMe } from '../types';
import { EnvBadge, isLive } from './EnvBadge';
import { SkewBanner } from './SkewBanner';

export interface LayoutProps {
  me: GmMe;
  skew: VersionSkew;
  navigation: ReactNode;
  onSignOut: () => void;
  children: ReactNode;
}

/** Footer text: what the console was built for and what the server runs (FR-056). */
export function versionLine(skew: VersionSkew): string {
  const built = skew.builtFor.gameVersion
    ? `framework ${skew.builtFor.frameworkVersion}, game ${skew.builtFor.gameVersion}`
    : `framework ${skew.builtFor.frameworkVersion}`;
  return `Console built for ${built} · server framework ${skew.serverFrameworkVersion || '(none)'}, game ${skew.serverGameVersion || '(none)'}`;
}

/** Console frame (FR-060): header with environment, game and staff; navigation; skew banner; version footer. Plain CSS grid. */
export function Layout({ me, skew, navigation, onSignOut, children }: LayoutProps) {
  const live = isLive(me.environmentId);
  return (
    <div className="gm-layout" data-live={live || undefined}>
      {live && (
        <div className="gm-live-strip" role="note">
          LIVE environment — every action affects real players
        </div>
      )}
      <header className="gm-header">
        <Group gap="sm" wrap="wrap">
          <Text fw={700} component="span">
            GM console
          </Text>
          <EnvBadge environmentId={me.environmentId} />
          <Text component="span" size="sm" aria-label={`Game ${me.gameId}`}>
            {me.gameId}
          </Text>
        </Group>
        <Group gap="sm" wrap="wrap">
          <Text component="span" size="sm" data-testid="staff-name">
            {me.displayName}
          </Text>
          <Button size="xs" variant="default" onClick={onSignOut}>
            Sign out
          </Button>
        </Group>
      </header>
      <nav className="gm-nav" aria-label="Pages">
        {navigation}
      </nav>
      <main className="gm-main">
        <SkewBanner skew={skew} />
        {children}
      </main>
      <footer className="gm-footer">
        <Text size="xs" c="dimmed">
          {versionLine(skew)}
        </Text>
        {skew.gameDiffers && (
          <Text size="xs" c="dimmed" data-testid="game-version-info">
            Game version differs: this console was built for {skew.builtFor.gameVersion}, the server runs {skew.serverGameVersion}.
          </Text>
        )}
      </footer>
    </div>
  );
}
