// Passkeys in the browser, behind an interface so screens and tests never touch navigator.credentials.
import { PasskeyCancelledError, PasskeyUnsupportedError } from '@/lib/auth/errors';

/** WebAuthn options and credentials travel as JSON (the W3C "JSON" forms). */
export type PasskeyJson = Record<string, unknown>;

export interface PasskeyClient {
  isSupported(): boolean;
  /** Makes a new passkey from the server's creation options; returns the credential to send back. */
  create(options: PasskeyJson): Promise<PasskeyJson>;
  /** Signs a challenge with an existing passkey; returns the assertion to send back. */
  get(options: PasskeyJson): Promise<PasskeyJson>;
}

type WebAuthnGlobals = {
  parseCreationOptionsFromJSON?: (json: PasskeyJson) => unknown;
  parseRequestOptionsFromJSON?: (json: PasskeyJson) => unknown;
};
type JsonCredential = { toJSON(): PasskeyJson };

const cancelled = (error: unknown) =>
  typeof error === 'object' && error !== null && ['NotAllowedError', 'AbortError'].includes((error as { name?: string }).name ?? '');

/** The real thing, using the browser's own passkey prompt (Windows Hello, Face ID, a phone). */
export class BrowserPasskeyClient implements PasskeyClient {
  isSupported(): boolean {
    const pk = (globalThis as unknown as { PublicKeyCredential?: WebAuthnGlobals }).PublicKeyCredential;
    return (
      typeof navigator !== 'undefined' &&
      !!navigator.credentials &&
      !!pk &&
      typeof pk.parseCreationOptionsFromJSON === 'function' &&
      typeof pk.parseRequestOptionsFromJSON === 'function'
    );
  }

  async create(options: PasskeyJson): Promise<PasskeyJson> {
    if (!this.isSupported()) throw new PasskeyUnsupportedError();
    const pk = (globalThis as unknown as { PublicKeyCredential: WebAuthnGlobals }).PublicKeyCredential;
    try {
      const publicKey = pk.parseCreationOptionsFromJSON!(options) as PublicKeyCredentialCreationOptions;
      const credential = await navigator.credentials.create({ publicKey });
      if (!credential) throw new PasskeyCancelledError();
      return (credential as unknown as JsonCredential).toJSON();
    } catch (error) {
      throw cancelled(error) ? new PasskeyCancelledError() : error;
    }
  }

  async get(options: PasskeyJson): Promise<PasskeyJson> {
    if (!this.isSupported()) throw new PasskeyUnsupportedError();
    const pk = (globalThis as unknown as { PublicKeyCredential: WebAuthnGlobals }).PublicKeyCredential;
    try {
      const publicKey = pk.parseRequestOptionsFromJSON!(options) as PublicKeyCredentialRequestOptions;
      const credential = await navigator.credentials.get({ publicKey });
      if (!credential) throw new PasskeyCancelledError();
      return (credential as unknown as JsonCredential).toJSON();
    } catch (error) {
      throw cancelled(error) ? new PasskeyCancelledError() : error;
    }
  }
}

/** A scripted passkey client for tests and the mock-mode web app. */
export class FakePasskeyClient implements PasskeyClient {
  supported = true;
  /** What the next prompt does: approve it, or have the person dismiss it. */
  outcome: 'approve' | 'cancel' = 'approve';
  created: PasskeyJson[] = [];
  asserted: PasskeyJson[] = [];

  isSupported() {
    return this.supported;
  }

  async create(options: PasskeyJson) {
    if (!this.supported) throw new PasskeyUnsupportedError();
    if (this.outcome === 'cancel') throw new PasskeyCancelledError();
    this.created.push(options);
    return { id: 'fake-credential', type: 'public-key', response: { attestationObject: 'fake' } };
  }

  async get(options: PasskeyJson) {
    if (!this.supported) throw new PasskeyUnsupportedError();
    if (this.outcome === 'cancel') throw new PasskeyCancelledError();
    this.asserted.push(options);
    return { id: 'fake-credential', type: 'public-key', response: { signature: 'fake' } };
  }
}
