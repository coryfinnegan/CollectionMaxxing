import { expect, test } from './fixtures';

// An axe scan on each main page: no serious or critical violations.
test.describe('accessibility', () => {
  test('sign-in, setup and join', async ({ page, open, expectAccessible }) => {
    await open('/login');
    await expect(page.getByRole('button', { name: 'Sign in with a passkey' })).toBeVisible();
    await expectAccessible();

    await page.getByRole('button', { name: 'First time here? Set up CollectionMaxxing' }).click();
    await expect(page.getByRole('heading', { name: 'Set up CollectionMaxxing' })).toBeVisible();
    await expectAccessible();

    await page.goto('/join/valid-invite');
    await expect(page.getByRole('heading', { name: "Join Cory's library as Editor" })).toBeVisible();
    await expectAccessible();
  });

  test('library table, grid, item detail and edit', async ({ page, open, expectAccessible }) => {
    await open('/library');
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    await expectAccessible();

    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await expectAccessible();

    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await expectAccessible();

    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeVisible();
    await expectAccessible();
  });

  test('members and the invite link', async ({ page, open, expectAccessible }) => {
    await open('/members');
    await expect(page.getByText('Cory (you)')).toBeVisible();
    await expectAccessible();

    await page.getByRole('button', { name: 'Invite someone' }).click();
    await page.getByRole('textbox', { name: 'Name' }).fill('Taylor');
    await page.getByRole('button', { name: 'Invite as Viewer' }).click();
    await page.getByRole('button', { name: 'Create invite link' }).click();
    await expect(page.getByLabel('Invite link', { exact: true })).toBeVisible();
    await expectAccessible();
  });
});
