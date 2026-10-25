// Visual regression for the key screens (#307): committed snapshots, compared by
// Playwright on every e2e run at zero cost. Regenerate deliberately with
// `npx playwright test visual --update-snapshots` and review the diff.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

const matrix = 'c0000000-0000-4000-8000-000000000001';

async function expectShot(page: Page, name: string) {
  // Fonts gate the first screen, but rasterization settles on its own clock.
  await page.evaluate(() => document.fonts.ready);
  // Scrollbar styling differs per OS; it is chrome, not the design under test.
  await page.addStyleTag({ content: '* { scrollbar-width: none !important; } *::-webkit-scrollbar { display: none !important; }' });
  // 0.015 absorbs measured Linux-vs-Windows rasterization (PR #329 CI: 0.01 on
  // library-table/members, 0.03 on about before unhinted rendering) while a
  // real regression still fails: a doubled title moved about by 0.03.
  await expect(page).toHaveScreenshot(`${name}.png`, { animations: 'disabled', maxDiffPixelRatio: 0.015 });
}

test.describe('visual', () => {
  test('library table', async ({ page, open }) => {
    await open('/library');
    await expect(page.getByRole('grid', { name: 'Library table' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    await expectShot(page, 'library-table');
  });

  test('library grid', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await expectShot(page, 'library-grid');
  });

  test('item detail', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}$`));
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await expectShot(page, 'item-detail');
  });

  // Logged-out routes show no authenticated nav (#328): no sidebar beside the sign-in form.
  test('sign-in', async ({ page, open }) => {
    await open('/login');
    await expect(page.getByText('Sign in to your library.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    await expectShot(page, 'sign-in');
  });

  test('members', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\/members$/);
    await expect(page.getByText('Cory (you)')).toBeVisible();
    await expectShot(page, 'members');
  });

  test('about', async ({ page, open }) => {
    await open('/settings');
    await page.getByRole('button', { name: 'About and privacy' }).click();
    await expect(page).toHaveURL(/\/about$/);
    await expect(page.getByText('Provider data')).toBeVisible();
    await expectShot(page, 'about');
  });
});
