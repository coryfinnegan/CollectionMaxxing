// The way-back sweep (#356): every pushed screen shows exactly one visible back
// or close control with an accessible name and a 44px target — never
// gesture-only, never stranded. Fresh loads (no history) land on the parent hub.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const RESULT = 'c1000000-0000-4000-8000-000000000001';

// Pushed pages: the layout owns the way back (header on the phone, a row above
// the content on the web), so each screen shows exactly one.
const pages = [
  '/wishlist',
  '/lent-out',
  '/members',
  '/settings',
  '/about',
  '/me/locations',
  '/me/export',
  '/me/devices',
  '/me/sign-in-methods',
  '/me/deleted',
  '/me/delete',
];

// Pushed root screens: the shared header back shows even on a fresh load,
// where the native back would have nothing to go back to.
const pushed = [`/item/${MATRIX}`, `/item/${MATRIX}/edit`, '/review', `/result/${RESULT}`, '/evals'];

async function expectWayBack(page: Page, name: string) {
  const back = page.getByRole('button', { name, exact: true });
  await expect(back).toHaveCount(1);
  await expect(back).toBeVisible();
  const box = (await back.boundingBox())!;
  expect(box.width).toBeGreaterThanOrEqual(44);
  expect(box.height).toBeGreaterThanOrEqual(44);
  // No route-group name leaks into a visible header (#372).
  await expect(page.getByText(/\(tabs\)|\(pages\)/)).toHaveCount(0);
}

test.describe('way back', () => {
  for (const path of pages) {
    test(`${path} shows one 44px back button`, async ({ page, open }) => {
      await open(path);
      await expectWayBack(page, 'Back');
    });
  }

  for (const path of pushed) {
    test(`${path} shows one 44px back button`, async ({ page, open }) => {
      await open(path);
      await expectWayBack(page, 'Back');
    });
  }

  test('the scanner shows a close button', async ({ page, open }) => {
    await open('/scanner');
    await expectWayBack(page, 'Close');
  });

  test('the library flows show a back button', async ({ page, open }) => {
    await open('/welcome/create');
    await expectWayBack(page, 'Back');
    await open('/welcome/join');
    await expectWayBack(page, 'Back');
  });

  test('back from a fresh settings load lands on Me', async ({ page, open }) => {
    await open('/settings');
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/me$/);
  });

  test('back from a fresh item load lands on the library', async ({ page, open }) => {
    await open(`/item/${MATRIX}`);
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/library$/);
  });

  test('back after pushing an item returns to the library', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${MATRIX}$`));
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/library$/);
  });

  test('back from about returns to settings', async ({ page, open }) => {
    await open('/settings');
    await page.getByRole('button', { name: 'About and privacy' }).click();
    await expect(page).toHaveURL(/\/about$/);
    await expectWayBack(page, 'Back');
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/settings$/);
  });
});
