import { Alert, Loader } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { problemOf } from '../api/problem';
import { NOT_FOUND } from '../pages/paths';
import { ErrorDisplay } from './ErrorDisplay';

export interface QueryStateProps<T> {
  query: UseQueryResult<T>;
  /** Text for a 404 answer (e.g. "Player not found."). */
  notFound?: string;
  children: (data: T) => ReactNode;
}

/** Loading, error (404 as its own text) and success of one read. */
export function QueryState<T>({ query, notFound, children }: QueryStateProps<T>) {
  if (query.isPending) {
    return <Loader size="sm" aria-label="Loading" />;
  }

  if (query.isError) {
    const problem = problemOf(query.error);
    return notFound !== undefined && problem.status === NOT_FOUND ? (
      <Alert color="gray" role="alert">
        {notFound}
      </Alert>
    ) : (
      <ErrorDisplay problem={problem} />
    );
  }

  return <>{children(query.data)}</>;
}
