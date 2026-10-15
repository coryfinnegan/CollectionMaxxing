import { expect, test } from './fixtures';

test('the export boots and the library renders with no console errors', async ({ page, open, errors }) => {
  await open('/');
  await expect(page).toHaveURL(/\/library$/);
  await expect(page.getByText('The Matrix').first()).toBeVisible();
  expect(errors).toEqual([]);
});
