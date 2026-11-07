import { Alert } from '@mantine/core';
import type { VersionSkew } from '../session/versions';

/** Warning when the server's framework `major.minor` differs from the build's, or cannot be compared (FR-056). Never blocks. */
export function SkewBanner({ skew }: { skew: VersionSkew }) {
  if (skew.framework === 'same') {
    return null;
  }

  const versions = `This console was built for framework ${skew.builtFor.frameworkVersion}; the server runs ${skew.serverFrameworkVersion || '(none)'}.`;
  return (
    <Alert color="yellow" title={skew.framework === 'unparsable' ? 'Versions cannot be compared' : 'Version mismatch'} role="status">
      {versions} Some pages may fail until the console built from the same release is deployed.
    </Alert>
  );
}
