/** The person dismissed the passkey prompt. */
export class PasskeyCancelledError extends Error {
  constructor() {
    super('Passkey prompt cancelled');
    this.name = 'PasskeyCancelledError';
  }
}

/** This browser has no passkey support. */
export class PasskeyUnsupportedError extends Error {
  constructor() {
    super('Passkeys are not supported here');
    this.name = 'PasskeyUnsupportedError';
  }
}
