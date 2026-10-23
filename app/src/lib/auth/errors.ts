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

/** The person chose "Not now" on the confirm-it's-you prompt. */
export class ReauthCancelledError extends Error {
  constructor() {
    super('Sign-in confirmation cancelled');
    this.name = 'ReauthCancelledError';
  }
}
