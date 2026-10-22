import type { Page } from '@playwright/test';

/**
 * Stands in for the server's `GET /account/login` (which hands off to Auth0; there is none in CI). Every navigation to it is
 * recorded, and answered with a redirect to `landing` so the journey can carry on, or with a plain page.
 */
export async function fakeSignIn(page: Page, landing?: string): Promise<{ urls: string[] }> {
  const urls: string[] = [];
  await page.route('**/account/login**', async (route) => {
    const url = new URL(route.request().url());
    urls.push(`${url.pathname}${url.search}`);
    if (landing) await route.fulfill({ status: 302, headers: { location: landing } });
    else await route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Auth0</title><p>Auth0 sign-in</p>' });
  });
  return { urls };
}
