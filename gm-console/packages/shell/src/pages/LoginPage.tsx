import { Alert, Button, Divider, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { ErrorDisplay } from '../components/ErrorDisplay';
import { useSession } from '../session/SessionProvider';
import type { SignedOutNotice } from '../session/phases';
import type { GmProblem } from '../types';

/** The denial value of the SSO callback (`BaseUrl#/login?error=sso_denied`, contract console-hosting §6). */
export const SSO_DENIED = 'sso_denied';

const NOTICES: Record<SignedOutNotice, string> = {
  expired: 'Your session has ended. Sign in again to continue.',
  'signed-out': 'You are signed out.',
  'sign-out-unconfirmed': 'You are signed out of this console, but the GM API did not confirm the sign-out.',
};

const INVALID_CREDENTIALS = 'Invalid staff id or password.';

/**
 * Login page (FR-053, FR-054, US6-2, US6-3): the local form exactly when `auth/methods.local`, the SSO button exactly when
 * `sso`, a message when neither. The password lives only in this form and is cleared after every submit.
 */
export function LoginPage() {
  const { state, signInLocal, startSso } = useSession();
  const [searchParams] = useSearchParams();
  const [staffId, setStaffId] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<GmProblem | undefined>(undefined);

  if (state.phase !== 'signed-out') {
    return null;
  }

  const { methods, notice } = state;
  const denied = searchParams.get('error') === SSO_DENIED;

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSubmitting(true);
    setFailure(undefined);
    const submittedPassword = password;
    setPassword('');
    const problem = await signInLocal(staffId.trim(), submittedPassword);
    setSubmitting(false);
    setFailure(problem);
  };

  return (
    <main className="gm-center">
      <Stack maw="24rem" w="100%">
        <Title order={1} size="h2">
          GM console
        </Title>
        <Text size="sm" c="dimmed">
          {state.services.config.gameId} · {state.services.config.environmentId}
        </Text>
        {notice && <Alert color="blue">{NOTICES[notice]}</Alert>}
        {denied && (
          <Alert color="red" title="Sign-in denied" role="alert">
            The identity provider sign-in was not accepted for the GM console.
          </Alert>
        )}
        {methods.local && (
          <form onSubmit={(event) => void submit(event)} aria-label="Local sign-in">
            <Stack>
              <TextInput label="Staff id" value={staffId} onChange={(e) => setStaffId(e.currentTarget.value)} required autoComplete="username" />
              <PasswordInput
                label="Password"
                value={password}
                onChange={(e) => setPassword(e.currentTarget.value)}
                required
                autoComplete="current-password"
              />
              <Button type="submit" loading={submitting}>
                Sign in
              </Button>
            </Stack>
          </form>
        )}
        {failure &&
          (failure.status === 401 ? (
            <Alert color="red" role="alert">
              {INVALID_CREDENTIALS}
            </Alert>
          ) : (
            <ErrorDisplay problem={failure} />
          ))}
        {methods.local && methods.sso && <Divider label="or" />}
        {methods.sso && (
          <Button variant="default" onClick={startSso}>
            Sign in with SSO
          </Button>
        )}
        {!methods.local && !methods.sso && (
          <Alert color="yellow" role="alert">
            No sign-in method is enabled on this GM API.
          </Alert>
        )}
      </Stack>
    </main>
  );
}
