import { expect, test } from './fixtures';

// An axe scan on every other page of the web build (a11y.spec.ts covers sign-in, the library and Members):
// no serious or critical violations. Each test opens its page with a first load, because a reload resets the mock data.
const pages: { name: string; path: string; heading: string | RegExp | null }[] = [
  { name: 'wishlist', path: '/wishlist', heading: 'Wishlist' },
  { name: 'lent out', path: '/lent-out', heading: 'Lent out' },
  { name: 'locations', path: '/me/locations', heading: 'Locations' },
  { name: 'import and export', path: '/me/export', heading: 'Export my data' },
  { name: 'recently deleted (trash)', path: '/me/deleted', heading: 'Recently deleted' },
  { name: 'devices and browsers', path: '/me/devices', heading: /Devices and browsers/ },
  { name: 'sign-in methods', path: '/me/sign-in-methods', heading: /Sign-in methods/ },
  { name: 'welcome', path: '/welcome?account=new', heading: /Welcome/ },
  { name: 'create your library', path: '/welcome/create', heading: 'Create your library' },
  { name: 'join a library', path: '/welcome/join', heading: 'Join a library' },
  { name: 'delete account', path: '/me/delete', heading: 'Delete account' },
  { name: 'me', path: '/me', heading: 'Me' },
  { name: 'settings', path: '/settings', heading: 'Settings' },
  { name: 'about', path: '/about', heading: 'About' },
  { name: 'evals', path: '/evals', heading: 'Run evals' },
  { name: 'scanner (web stand-in)', path: '/scanner', heading: 'Scanner' },
  { name: 'review', path: '/review', heading: /Review/ },
  { name: 'result detail', path: '/result/c1000000-0000-4000-8000-000000000001', heading: null },
  { name: 'search results (fixture)', path: '/search?state=results', heading: null },
];

test.describe('accessibility of every page', () => {
  for (const { name, path, heading } of pages) {
    test(`${name} has no serious or critical axe violations`, async ({ page, open, expectAccessible }) => {
      await open(path);
      if (heading) await expect(page.getByRole('heading', { name: heading }).first()).toBeVisible();
      else await page.waitForLoadState('networkidle');
      await expectAccessible();
    });
  }
});

test.describe('accessibility in dark mode', () => {
  for (const path of ['/library', '/members', '/settings', '/about', '/me/export']) {
    test(`${path} has no serious or critical axe violations`, async ({ page, open, expectAccessible }) => {
      await page.emulateMedia({ colorScheme: 'dark' });
      await open(path);
      await expect(page.getByRole('heading').first()).toBeVisible();
      await expectAccessible();
    });
  }
});
