import { Code } from '@mantine/core';

export interface JsonViewProps {
  value: unknown;
  /** Accessible name of the block. */
  label?: string;
}

/** Server data as JSON text (FR-076, SEC-12): `JSON.stringify(value, null, 2)` in a `<pre>`, never HTML. */
export function JsonView({ value, label }: JsonViewProps) {
  const text = JSON.stringify(value, null, 2) ?? String(value);
  return (
    <Code block aria-label={label} className="gm-json">
      {text}
    </Code>
  );
}
