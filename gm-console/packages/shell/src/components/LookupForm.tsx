import { Alert, Button, Group, TextInput } from '@mantine/core';
import { useState, type FormEvent } from 'react';
import { NOT_FOUND } from '../pages/paths';
import type { GmProblem } from '../types';
import { ErrorDisplay } from './ErrorDisplay';

export interface LookupFormProps {
  label: string;
  description?: string;
  /** Runs the lookup; resolves with the problem when it failed. */
  onLookup: (value: string) => Promise<GmProblem | undefined>;
  notFound: string;
}

/** One input + button; the value stays in component state only (FR-061: never in the URL). */
export function LookupForm({ label, description, onLookup, notFound }: LookupFormProps) {
  const [value, setValue] = useState('');
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<GmProblem | undefined>(undefined);

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = value.trim();
    if (trimmed === '') {
      return;
    }

    setPending(true);
    setProblem(undefined);
    const failure = await onLookup(trimmed);
    setPending(false);
    setProblem(failure);
  };

  return (
    <form onSubmit={(event) => void submit(event)} aria-label={`${label} lookup`}>
      <Group align="flex-end" gap="sm" wrap="wrap">
        <TextInput label={label} description={description} value={value} onChange={(e) => setValue(e.currentTarget.value)} />
        <Button type="submit" loading={pending}>
          Find
        </Button>
      </Group>
      {problem &&
        (problem.status === NOT_FOUND ? (
          <Alert color="gray" role="alert" mt="xs">
            {notFound}
          </Alert>
        ) : (
          <ErrorDisplay problem={problem} />
        ))}
    </form>
  );
}
