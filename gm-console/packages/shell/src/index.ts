import '@mantine/core/styles.css';
import './components/layout.css';
import { createElement, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { GmConsoleApp } from './App';
import { readBuiltFor } from './session/builtFor';

export type { GmBuiltFor, GmConsoleConfig, GmMe, GmProblem } from './types';

/** Options of `createGmConsole` (contract public-api §6). GM-4 adds `extensions` additively. */
export interface GmConsoleOptions {
  root?: HTMLElement;
}

/** Element id the default `index.html` provides. */
const ROOT_ELEMENT_ID = 'root';

/** Mounts the console into `#root` (or the given element). */
export function createGmConsole(options: GmConsoleOptions = {}): void {
  const element = options.root ?? document.getElementById(ROOT_ELEMENT_ID);
  if (!element) {
    throw new Error(`createGmConsole: no #${ROOT_ELEMENT_ID} element and no root option.`);
  }

  createRoot(element).render(createElement(StrictMode, null, createElement(GmConsoleApp, { builtFor: readBuiltFor() })));
}
