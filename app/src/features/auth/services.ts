// The swappable pieces of the auth screens, set once at app start (and replaced by fakes in tests).
import type { ReactElement } from 'react';
import { QrCode } from './QrCode';

export type QrRenderer = (props: { value: string; size: number }) => ReactElement;

/** Leaves the app for a URL (the sign-in, or Auth0's sign-out). Tests swap it for a spy. */
export type Navigator = (url: string) => void;

const browserNavigator: Navigator = (url) => {
  (globalThis as { location?: { assign: (url: string) => void } }).location?.assign(url);
};

let navigator: Navigator = browserNavigator;
let qrRenderer: QrRenderer = QrCode;

export const getNavigator = () => navigator;
export const setNavigator = (next: Navigator) => {
  navigator = next;
};
export const getQrRenderer = () => qrRenderer;
export const setQrRenderer = (next: QrRenderer) => {
  qrRenderer = next;
};
