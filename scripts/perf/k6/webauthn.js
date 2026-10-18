// A WebAuthn authenticator in software, for the load test's seed step only (the same job as the test suite's SoftwareAuthenticator):
// it answers the passkey creation options the API sends, so the seed can sign in through the API's own flows. Attestation "none", ES256.
import encoding from 'k6/encoding';

const text = (s) => {
  // ASCII only (k6 has no TextEncoder): every string in these structures is ASCII.
  const out = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i) & 0x7f;
  return out;
};

const concat = (...parts) => {
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
};

export const b64url = (bytes) => encoding.b64encode(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.length), 'rawurl');

export class SoftwareAuthenticator {
  constructor(rpId, origin, keys, credentialId) {
    this.rpId = rpId;
    this.origin = origin;
    this.keys = keys;
    this.credentialId = credentialId;
  }

  static async create(rpId, origin) {
    const keys = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
    const credentialId = new Uint8Array(32);
    crypto.getRandomValues(credentialId);
    return new SoftwareAuthenticator(rpId, origin, keys, credentialId);
  }

  /** Answers the creation options the server sent (the `options` object), like navigator.credentials.create(). */
  async createCredential(options) {
    const clientData = text(JSON.stringify({ type: 'webauthn.create', challenge: options.challenge, origin: this.origin, crossOrigin: false }));
    const rpIdHash = new Uint8Array(await crypto.subtle.digest('SHA-256', text(this.rpId)));
    const publicKey = new Uint8Array(await crypto.subtle.exportKey('raw', this.keys.publicKey)); // 0x04 | X | Y
    const cose = concat(
      new Uint8Array([0xa5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20]), publicKey.slice(1, 33),
      new Uint8Array([0x22, 0x58, 0x20]), publicKey.slice(33, 65));
    const authData = concat(
      rpIdHash, new Uint8Array([0x45, 0, 0, 0, 1]), new Uint8Array(16),
      new Uint8Array([this.credentialId.length >> 8, this.credentialId.length & 0xff]), this.credentialId, cose);
    // { "fmt": "none", "attStmt": {}, "authData": h'...' } as CBOR
    const attestation = concat(
      new Uint8Array([0xa3, 0x63]), text('fmt'), new Uint8Array([0x64]), text('none'),
      new Uint8Array([0x67]), text('attStmt'), new Uint8Array([0xa0, 0x68]), text('authData'),
      new Uint8Array([0x59, authData.length >> 8, authData.length & 0xff]), authData);
    const id = b64url(this.credentialId);
    return {
      id,
      rawId: id,
      type: 'public-key',
      authenticatorAttachment: 'platform',
      clientExtensionResults: {},
      response: { clientDataJSON: b64url(clientData), attestationObject: b64url(attestation), transports: ['internal'] },
    };
  }
}
