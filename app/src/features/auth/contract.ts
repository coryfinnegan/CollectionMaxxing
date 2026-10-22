// The Auth0 sign-in routes (issues #228 and #229, with the lead's contract addenda), typed by hand because they are not in
// contracts/openapi.json yet. Replaced by the generated types once #228 merges.

/** `POST /setup` and `POST /account/recover`: the next step is a browser navigation to the sign-in. */
export type NextStepDto = { next: string };

/** `POST /account/logout`: where to send the browser to end the Auth0 session too. */
export type LogoutDto = { logoutUrl: string };

export type SignInProvider = 'google' | 'apple' | 'other';

export type SignInMethodDto = {
  id: string;
  provider: SignInProvider;
  email: string | null;
  createdAt: string;
  lastUsedAt: string | null;
  /** The identity behind this session. */
  isCurrent: boolean;
};
export type PageOfSignInMethodDto = { items: SignInMethodDto[]; nextCursor: string | null };

export type AppSignInDto = { id: string; deviceName: string; createdAt: string; lastSeenAt: string | null };
export type PageOfAppSignInDto = { items: AppSignInDto[]; nextCursor: string | null };

/** Why a sign-in came back to the app without finishing (`/login?error=…` and `returnUrl?error=…`). */
export type SignInError = 'signin_failed' | 'invite_invalid' | 'setup_closed' | 'already_linked' | 'reauth_failed';
