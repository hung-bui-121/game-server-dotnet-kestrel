import { render, screen, within } from '@testing-library/react';
import type { ReactElement } from 'react';
import { describe, expect, it } from 'vitest';
import { networkProblem } from '../api/problem';
import { createTestQueryClient, TestProviders } from '../test/render';
import { ErrorDisplay } from './ErrorDisplay';
import { JsonView } from './JsonView';

function show(ui: ReactElement) {
  return render(<TestProviders client={createTestQueryClient()}>{ui}</TestProviders>);
}

const HOSTILE = '<img src=x onerror=alert(1)>';

describe('ErrorDisplay (FR-062, US7-6)', () => {
  it('shows title, code and number, status, detail, every field error and the request id', () => {
    show(
      <ErrorDisplay
        problem={{
          status: 400,
          code: 'VALIDATION_FAILED',
          codeNumber: 4001,
          title: 'Invalid request',
          detail: 'Fix the fields.',
          retryable: false,
          errors: { pageSize: ['must be between 1 and 100'], cursor: ['is not a cursor', 'is too long'] },
          requestId: 'req-42',
        }}
      />,
    );

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('Invalid request');
    expect(screen.getByTestId('problem-code')).toHaveTextContent('VALIDATION_FAILED (4001) · HTTP 400');
    expect(alert).toHaveTextContent('Fix the fields.');
    const fields = within(screen.getByRole('list', { name: 'Field errors' })).getAllByRole('listitem').map((i) => i.textContent);
    expect(fields).toEqual(['pageSize: must be between 1 and 100', 'cursor: is not a cursor is too long']);
    expect(alert).toHaveTextContent('Request id: req-42');
  });

  it('says there was no answer from the API origin on a network failure', () => {
    show(<ErrorDisplay problem={networkProblem('http://localhost:5080/x')} />);

    expect(screen.getByRole('alert')).toHaveTextContent('No answer from the GM API at http://localhost:5080.');
    expect(screen.queryByTestId('problem-code')).toBeNull();
  });

  it('renders server strings as text', () => {
    show(<ErrorDisplay problem={{ status: 500, code: 'X', retryable: false, errors: { [HOSTILE]: [HOSTILE] }, detail: HOSTILE, title: HOSTILE }} />);

    expect(document.querySelector('img')).toBeNull();
    expect(screen.getByRole('alert')).toHaveTextContent(HOSTILE);
  });
});

describe('JsonView (FR-076)', () => {
  it('renders JSON.stringify(value, null, 2) in a pre as text, creating no element from the data', () => {
    const value = { name: HOSTILE, nested: { list: [1, 'two'] } };
    show(<JsonView value={value} label="state" />);

    const block = screen.getByLabelText('state');
    expect(block.tagName).toBe('PRE');
    expect(block.textContent).toBe(JSON.stringify(value, null, 2));
    expect(document.querySelector('img')).toBeNull();
  });

  it('shows null and primitive values', () => {
    show(
      <>
        <JsonView value={null} label="a" />
        <JsonView value={42} label="b" />
        <JsonView value={undefined} label="c" />
      </>,
    );

    expect(screen.getByLabelText('a').textContent).toBe('null');
    expect(screen.getByLabelText('b').textContent).toBe('42');
    expect(screen.getByLabelText('c').textContent).toBe('undefined');
  });
});
