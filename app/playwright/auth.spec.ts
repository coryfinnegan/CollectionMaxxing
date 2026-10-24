import { expect, test } from './fixtures';
import { fakeSignIn } from './signin';

// Sign-in is a navigation to the server's /account/login, which hands off to Auth0. CI has no Auth0, so each spec answers that
// one navigation itself (see ./signin.ts) and checks the URL the app asked for.
test.describe('sign-in', () => {
  test('Sign in navigates to the server with a way back to the library, and lands in it', async ({ page, open, shot }) => {
    const signIn = await fakeSignIn(page, '/library');
    await open('/login');
    await expect(page.getByText('Sign in to your library.')).toBeVisible();
    await shot('sign-in');

    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page).toHaveURL(/\/library$/);
    expect(signIn.urls).toEqual(['/account/login?returnUrl=%2Flibrary']);
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
  });

  test('a failed sign-in comes back to the sign-in page with a message', async ({ page, open }) => {
    await open('/login?error=signin_failed');
    await expect(page.getByRole('alert')).toHaveText("Sign-in didn't finish. Try again.");
  });

  test('a stale session sends the library to sign-in', async ({ page, open }) => {
    await open('/library');
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    // The session is revoked; the next request that reaches the server notices.
    await page.evaluate(() => (window as unknown as { __cm: { expireSession: () => void } }).__cm.expireSession());
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
  });

  test('signing out leaves for the URL the server returns', async ({ page, open }) => {
    await open('/settings');
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
  });
});

test.describe('sign-in methods and devices', () => {
  test('Add goes to the sign-in in link mode', async ({ page, open }) => {
    const signIn = await fakeSignIn(page);
    await open('/me/sign-in-methods');
    await expect(page.getByText('Google (this sign-in)')).toBeVisible();

    await page.getByRole('button', { name: 'Add a sign-in method' }).click();

    await expect.poll(() => signIn.urls).toEqual(['/account/login?returnUrl=%2Fme%2Fsign-in-methods&link=1']);
  });

  test('removing a method asks first, and the last one cannot be removed', async ({ page, open }) => {
    await open('/me/sign-in-methods');
    await page.getByRole('button', { name: 'Remove Apple' }).click();
    await page.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(page.getByText('Apple', { exact: true })).toHaveCount(0);
    await expect(page.getByText(/only way to sign in/)).toBeVisible();
    await expect(page.getByRole('button', { name: /^Remove/ })).toHaveCount(0);
  });

  test('devices lists browsers and iPhones and signs all iPhones out', async ({ page, open }) => {
    await open('/me/devices');
    await expect(page.getByText('Safari on Mac')).toBeVisible();
    await expect(page.getByText('Cory’s iPhone')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add a device' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Sign out all iPhones' }).click();
    await page.getByRole('button', { name: 'Sign out all iPhones' }).first().click();

    await expect(page.getByText('Cory’s iPhone')).toHaveCount(0);
    await expect(page.getByText(/No iPhones yet/)).toBeVisible();
  });
});
