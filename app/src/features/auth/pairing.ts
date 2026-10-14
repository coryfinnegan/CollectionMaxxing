// iPhone pairing (docs/07 "iPhone: pair once with a QR code"): trade a single-use token or 6-digit code for device tokens.
// Neither the token nor the code is ever logged.
import { logger } from '@/lib/auth/log';
import { startSession } from '@/lib/auth/session';

export type PairingPayload = { serverUrl: string; token: string };

export class PairingError extends Error {
  readonly kind: 'qr' | 'scanner' | 'address' | 'code' | 'expired' | 'rate' | 'unreachable';

  constructor(kind: PairingError['kind']) {
    super(`Pairing failed: ${kind}`);
    this.name = 'PairingError';
    this.kind = kind;
  }
}

const copy: Record<PairingError['kind'], string> = {
  qr: "That QR code isn't from CollectionMaxxing. Scan the one on your computer.",
  scanner: "Scanning isn't ready on this phone yet. Enter the 6-digit code instead.",
  address: "That server address doesn't look right. Use the address you open on your computer.",
  code: "That code didn't work. Check it, or add the device again on your computer.",
  expired: 'That code has expired. Add the device again on your computer.',
  rate: 'Too many attempts. Wait a minute and try again.',
  unreachable: "Couldn't reach your server. Check you're on the same network or Tailscale, then try again.",
};

export const pairingMessage = (error: unknown): string =>
  error instanceof PairingError ? copy[error.kind] : copy.unreachable;

/** "pc.tailnet.ts.net" and "https://pc.tailnet.ts.net/" both become "https://pc.tailnet.ts.net". */
export function normalizeServerUrl(input: string): string {
  const trimmed = input.trim();
  if (!trimmed) throw new PairingError('address');
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  try {
    const url = new URL(withScheme);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new PairingError('address');
    return url.origin;
  } catch {
    throw new PairingError('address');
  }
}

/** The QR code holds `{ serverUrl, token }` as JSON. */
export function parsePairingPayload(raw: string): PairingPayload {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new PairingError('qr');
  }
  const { serverUrl, token } = (parsed ?? {}) as Partial<PairingPayload>;
  if (typeof serverUrl !== 'string' || typeof token !== 'string' || !token) throw new PairingError('qr');
  try {
    return { serverUrl: normalizeServerUrl(serverUrl), token };
  } catch {
    throw new PairingError('qr');
  }
}

export const DEVICE_NAME = 'This iPhone';

type Proof = { token: string; code?: never } | { code: string; token?: never };

/** Calls the token endpoint, then keeps the refresh token in the store and the access token in memory. */
export async function pairThisDevice(serverUrl: string, proof: Proof, deviceName = DEVICE_NAME): Promise<void> {
  const body = new URLSearchParams({ grant_type: 'urn:cm:device_pairing', device_name: deviceName });
  if (proof.token !== undefined) body.set('pairing_token', proof.token);
  else body.set('pairing_code', proof.code);

  let response: Response;
  try {
    response = await fetch(`${serverUrl}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
      body: body.toString(),
    });
  } catch {
    logger.warn('Pairing could not reach the server.');
    throw new PairingError('unreachable');
  }

  if (!response.ok) {
    logger.warn(`Pairing was refused (status ${response.status}).`);
    if (response.status === 429) throw new PairingError('rate');
    if (response.status === 410) throw new PairingError('expired');
    throw new PairingError(response.status === 400 || response.status === 401 || response.status === 404 ? 'code' : 'unreachable');
  }

  const tokens = (await response.json()) as { access_token?: string; refresh_token?: string };
  if (!tokens.access_token || !tokens.refresh_token) throw new PairingError('code');
  await startSession({ accessToken: tokens.access_token, refreshToken: tokens.refresh_token, serverUrl });
}
