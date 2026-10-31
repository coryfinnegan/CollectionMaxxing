import { expect, test } from './fixtures';

test('About shows the privacy note and the provider credits, reached from Settings', async ({ page, open, errors, shot }) => {
  await open('/settings');
  await page.getByRole('button', { name: 'About and privacy' }).click();
  await expect(page).toHaveURL(/\/about$/);
  await expect(page.getByText('Covers are read on your iPhone', { exact: false })).toBeVisible();
  await expect(page.getByText('A photo goes to your own PC in three cases.', { exact: false })).toBeVisible();
  await expect(page.getByText('a row you keep as unverified: its photo stays until you delete the row or link it to an item', { exact: false })).toBeVisible();
  await expect(page.getByText('Provider data')).toBeVisible();
  await expect(page.getByRole('link', { name: /^TMDB: https:/ })).toBeVisible();
  await shot('about');
  expect(errors).toEqual([]);
});
