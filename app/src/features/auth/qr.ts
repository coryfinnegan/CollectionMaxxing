// QR scanning (iPhone) and rendering (web) sit behind interfaces. The real scanner arrives with cm-vision (CAM);
// the QR renderer needs a package the lead adds (see the PR).
import type { ReactElement } from 'react';

export interface QrScanner {
  /** Resolves with the text of the code, or null if the person backed out. */
  scan(): Promise<string | null>;
}

export class QrScannerUnavailableError extends Error {
  constructor() {
    super('QR scanner is not available yet');
    this.name = 'QrScannerUnavailableError';
  }
}

/** Used until the camera scanner is wired. */
export class UnavailableQrScanner implements QrScanner {
  async scan(): Promise<string | null> {
    throw new QrScannerUnavailableError();
  }
}

/** Returns a fixed code; the dev menu and tests use it. */
export class FakeQrScanner implements QrScanner {
  private readonly result: string | null;

  constructor(result: string | null) {
    this.result = result;
  }

  async scan() {
    return this.result;
  }
}

export type QrRenderer = (props: { value: string; size: number }) => ReactElement;
