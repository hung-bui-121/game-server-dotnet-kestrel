import { Alert, List, Text } from '@mantine/core';
import { NETWORK_ERROR, NO_ANSWER_STATUS } from '../api/problem';
import type { GmProblem } from '../types';

export interface ErrorDisplayProps {
  problem: GmProblem;
  /** Heading; defaults to the problem title. */
  title?: string;
}

/**
 * A GM API error from its Problem Details (FR-062): title, code and number, detail, every field error, request id. A failure
 * without a readable answer says so with the API origin. Every value is rendered as text.
 */
export function ErrorDisplay({ problem, title }: ErrorDisplayProps) {
  const fields = Object.entries(problem.errors);
  const noAnswer = problem.code === NETWORK_ERROR;
  const heading = title ?? (noAnswer ? 'No answer from the GM API' : (problem.title ?? 'The request failed'));

  return (
    <Alert color="red" title={heading} role="alert">
      {problem.detail && <Text size="sm">{problem.detail}</Text>}
      {!noAnswer && (
        <Text size="sm" data-testid="problem-code">
          {problem.code}
          {problem.codeNumber !== undefined ? ` (${problem.codeNumber})` : ''}
          {problem.status !== NO_ANSWER_STATUS ? ` · HTTP ${problem.status}` : ''}
        </Text>
      )}
      {fields.length > 0 && (
        <List size="sm" aria-label="Field errors">
          {fields.map(([field, messages]) => (
            <List.Item key={field}>
              {field}: {messages.join(' ')}
            </List.Item>
          ))}
        </List>
      )}
      {problem.requestId && (
        <Text size="xs" c="dimmed">
          Request id: {problem.requestId}
        </Text>
      )}
    </Alert>
  );
}
