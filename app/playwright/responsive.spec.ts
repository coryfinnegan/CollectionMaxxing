import { expect, fitsWidth, test } from './fixtures';

// Phone width (375): the same flows render without horizontal page scroll.
test.use({ viewport: { width: 375, height: 812 } });

test.describe('phone width', () => {
  test('the library grid fits and opens an item', async ({ page, open, shot }) => {
    await open('/library');
    // With no saved choice the phone starts in the grid; the table is one tap away and the page itself must not scroll sideways.
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);
    await shot('phone-library');

    await page.getByRole('button', { name: 'Table view' }).click();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);
    await shot('phone-library-table');

    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);
    await shot('phone-item');
  });

  test('edit, sign-in, welcome and join fit', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);

    await page.goto('/login');
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);

    await page.goto('/welcome?account=new');
    await expect(page.getByRole('button', { name: 'Create your library' })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);

    await page.goto('/join/valid-invite');
    await expect(page.getByRole('heading', { name: "Join Cory's library as Editor" })).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);
  });

  test('members fit and the invite flow works', async ({ page, open, shot }) => {
    await open('/members');
    await expect(page.getByText('Cory (you)')).toBeVisible();
    expect(await fitsWidth(page)).toBe(true);
    await page.getByRole('button', { name: 'Invite someone' }).click();
    await page.getByRole('textbox', { name: 'Name' }).fill('Taylor');
    await page.getByRole('button', { name: 'Invite as Viewer' }).click();
    await page.getByRole('button', { name: 'Create invite link' }).click();
    await expect(page.getByLabel('Invite link', { exact: true })).toContainText('/join/');
    expect(await fitsWidth(page)).toBe(true);
    await shot('phone-invite');
  });

  test('the phone shows the tab bar instead of the sidebar', async ({ page, open }) => {
    await open('/library');
    await expect(page.getByRole('navigation').getByRole('link', { name: 'Wishlist' })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Library' })).toBeVisible();
  });

  test('the tab bar switches between Library and Me, with no Scan tab', async ({ page, open }) => {
    await open('/library');
    const library = page.getByRole('tab', { name: 'Library' });
    const me = page.getByRole('tab', { name: 'Me' });
    await expect(library).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Scan' })).toHaveCount(0);
    await expect(library).toHaveAttribute('aria-selected', 'true');

    await me.click();
    await expect(page.getByRole('heading', { name: 'Me', exact: true })).toBeVisible();
    await expect(me).toHaveAttribute('aria-selected', 'true');

    await library.click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await expect(library).toHaveAttribute('aria-selected', 'true');
  });
});
