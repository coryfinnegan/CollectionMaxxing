import { expect, test } from './fixtures';

test('About shows the privacy note and the provider credits, reached from Settings', async ({ page, open, errors, shot }) => {
  await open('/settings');
  await page.getByRole('button', { name: 'About and privacy' }).click();
  await expect(page).toHaveURL(/\/about$/);
  await expect(page.getByText('Your photos stay on this iPhone.', { exact: false })).toBeVisible();
  await expect(page.getByText('Provider data')).toBeVisible();
  await expect(page.getByRole('link', { name: /^TMDB: https:/ })).toBeVisible();
  await shot('about');
  expect(errors).toEqual([]);
});
