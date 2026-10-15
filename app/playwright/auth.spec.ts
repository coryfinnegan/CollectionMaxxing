import { expect, test } from './fixtures';

test.describe('first run and sign-in', () => {
  test('setup creates the owner with a passkey and lands in the library', async ({ page, open, shot }) => {
    // A virtual authenticator, so a real WebAuthn ceremony would also succeed in headless Chromium.
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', {
      options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true },
    });

    await open('/setup');
    await expect(page.getByRole('heading', { name: 'Set up CollectionMaxxing' })).toBeVisible();
    const create = page.getByRole('button', { name: 'Create passkey' });
    await expect(create).toBeDisabled();
    await page.getByRole('textbox', { name: 'Setup code' }).fill('CM-SETUP-OK');
    await page.getByRole('textbox', { name: 'Your name' }).fill('Cory');
    await shot('setup');
    await create.click();
    await expect(page).toHaveURL(/\/library$/);
    await expect(page.getByRole('heading', { name: 'Library', level: 1 })).toBeVisible();
  });

  test('a wrong setup code shows an error and stays on the setup page', async ({ page, open }) => {
    await open('/setup');
    await page.getByRole('textbox', { name: 'Setup code' }).fill('nope');
    await page.getByRole('textbox', { name: 'Your name' }).fill('Cory');
    await page.getByRole('button', { name: 'Create passkey' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page).toHaveURL(/\/setup$/);
  });

  test('signing in with a passkey opens the library', async ({ page, open, shot }) => {
    await open('/login');
    await expect(page.getByText('Sign in to your library.')).toBeVisible();
    await shot('sign-in');
    await page.getByRole('button', { name: 'Sign in with a passkey' }).click();
    await expect(page).toHaveURL(/\/library$/);
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
  });

  test('the sign-in page links to first-time setup', async ({ page, open }) => {
    await open('/login');
    await page.getByRole('button', { name: 'First time here? Set up CollectionMaxxing' }).click();
    await expect(page).toHaveURL(/\/setup$/);
  });

  test('a stale session sends the library to sign-in', async ({ page, open }) => {
    await open('/library');
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    // The session is revoked; the next request that reaches the server notices.
    await page.evaluate(() => (window as unknown as { __cm: { expireSession: () => void } }).__cm.expireSession());
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('button', { name: 'Sign in with a passkey' })).toBeVisible();
  });
});
