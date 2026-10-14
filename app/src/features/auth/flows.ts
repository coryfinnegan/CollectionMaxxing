// The browser passkey ceremonies. Each one checks for passkey support first so an old browser fails before any request.
import { PasskeyUnsupportedError } from '@/lib/auth/errors';
import { friendlyError } from '@/lib/problem';
import { ApiError } from '@/lib/http';
import { finishLogin, finishRegistration, requestLoginOptions, startJoin, startSetup } from './api';
import type { PasskeyClient } from './passkeys';

function ensureSupported(passkeys: PasskeyClient) {
  if (!passkeys.isSupported()) throw new PasskeyUnsupportedError();
}

/** First run: setup code and name, then the owner's first passkey. Signs the owner in. */
export async function setUpOwner(passkeys: PasskeyClient, input: { code: string; displayName: string }) {
  ensureSupported(passkeys);
  const { options } = await startSetup(input.code.trim(), input.displayName.trim());
  const credential = await passkeys.create(options);
  return finishRegistration(credential, { name: 'This browser' });
}

/** A family member accepting an invite: their name, then a passkey of their own. */
export async function joinWithInvite(passkeys: PasskeyClient, input: { token: string; displayName: string }) {
  ensureSupported(passkeys);
  const { options } = await startJoin(input.token, input.displayName.trim());
  const credential = await passkeys.create(options);
  return finishRegistration(credential, { name: 'This browser', invite: input.token });
}

/** Sign in with any passkey on this device. Also the "confirm it's you" prompt. */
export async function signInWithPasskey(passkeys: PasskeyClient) {
  ensureSupported(passkeys);
  const { options } = await requestLoginOptions();
  const credential = await passkeys.get(options);
  return finishLogin(credential);
}

export type FlowContext = 'setup' | 'join' | 'login';

const byContext: Record<FlowContext, Record<number, string>> = {
  setup: {
    400: "That setup code isn't right. Check the code on your computer and try again.",
    404: 'Setup is already finished. Sign in instead.',
    410: 'That setup code has expired. Restart the server on your computer to get a new one.',
  },
  join: {
    400: "This invite link isn't valid. Ask for a new one.",
    404: "This invite link isn't valid. Ask for a new one.",
    410: 'This invite has expired or was already used. Ask for a new one.',
  },
  login: {
    401: "That passkey wasn't accepted. Try again, or use another passkey.",
  },
};

/** Friendly copy for a failed ceremony. */
export function ceremonyMessage(error: unknown, context: FlowContext): string {
  if (error instanceof ApiError && byContext[context][error.status]) return byContext[context][error.status];
  return friendlyError(error);
}
