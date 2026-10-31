import { expect, test } from './fixtures';

const matrix = 'c0000000-0000-4000-8000-000000000001';
type Hooks = { __cm: { changeItemElsewhere: (id: string) => void } };

test.describe('library', () => {
  test('the table lists the seeded items and the grid shows covers', async ({ page, open, shot }) => {
    await open('/library');
    await expect(page.getByRole('grid', { name: 'Library table' })).toBeVisible();
    for (const title of ['The Matrix', 'Dune', 'Super Mario 64', 'Back to the Future']) {
      await expect(page.getByRole('link', { name: `Open ${title}` })).toBeVisible();
    }
    // The library is what you have: Hades is on the wishlist, so it is not here.
    await expect(page.getByRole('link', { name: 'Open Hades' })).toHaveCount(0);
    await shot('library-table');

    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Super Mario 64, Cartridge, Nintendo 64' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add item' })).toBeVisible();
    await shot('library-grid');
  });

  test('filter chips and search narrow the results', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Games', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Open Super Mario 64' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open GoldenEye 007' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toHaveCount(0);

    await page.getByRole('button', { name: 'All kinds' }).click();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();

    await page.getByRole('textbox', { name: 'Search' }).fill('dune');
    await expect(page.getByRole('link', { name: 'Open Dune' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toHaveCount(0);
  });

  test('sorting by year and by title reorders the rows', async ({ page, open }) => {
    await open('/library');
    const firstRowLink = page.getByRole('row').nth(1).getByRole('link');
    await expect(firstRowLink).toHaveText('The Matrix');
    await page.getByRole('button', { name: 'Sort by Year' }).click();
    await expect(firstRowLink).toHaveText('Dune');
    await page.getByRole('button', { name: 'Sort by Title' }).click();
    await expect(firstRowLink).toHaveText('Back to the Future');
  });

  test('an item opens, shows its facts, and edits save', async ({ page, open, shot }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}$`));
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await expect(page.getByText('Location', { exact: true }).locator('visible=true')).toBeVisible();
    await expect(page.getByText('Living room').locator('visible=true').first()).toBeVisible();
    await shot('item-detail');

    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}/edit$`));
    // Missing is still yours, so the row stays in the library (Wishlist or Sold would take it out; the table spec covers that).
    await page.getByRole('button', { name: 'Status Missing' }).click();
    await page.getByRole('textbox', { name: 'Notes' }).fill('Red pill edition');
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect(page).toHaveURL(new RegExp(`/item/${matrix}$`));
    await expect(page.getByText('Red pill edition').locator('visible=true').first()).toBeVisible();
    await page.goBack();
    await expect(page.getByRole('button', { name: 'Change status of The Matrix, now Missing' })).toBeVisible();
  });

  // On the wide web the Me pages are listed in the sidebar, since there is no Me tab: Sold for everyone, Recently deleted for editors.
  test('the sidebar reaches Sold and Recently deleted', async ({ page, open }) => {
    await open('/library');
    const nav = page.getByRole('navigation');
    await expect(nav.getByRole('link', { name: 'Recently deleted', exact: true })).toBeVisible();

    await nav.getByRole('link', { name: 'Sold', exact: true }).click();
    await expect(page).toHaveURL(/\/sold$/);
    await expect(page.getByRole('heading', { name: 'Sold' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open Tetris' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toHaveCount(0);

    await nav.getByRole('link', { name: 'Recently deleted', exact: true }).click();
    await expect(page).toHaveURL(/\/me\/deleted$/);
    await expect(page.getByText('Deleted items stay here for 30 days.')).toBeVisible();
  });

  test('item detail keeps the sidebar', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await expect(page.getByRole('navigation').getByRole('link', { name: 'Wishlist', exact: true })).toBeVisible();
  });

  test('a stale ETag shows the reload message and keeps the edits', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('textbox', { name: 'Notes' }).fill('My edit');
    // Someone else saves first.
    await page.evaluate((id) => (window as unknown as Hooks).__cm.changeItemElsewhere(id), matrix);
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect(page.getByText('Someone changed this. Reload?')).toBeVisible();
    await expect(page.getByText('Your edits are still here.')).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Notes' })).toHaveValue('My edit');
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}/edit$`));
  });

  test('deleting shows Undo, and Undo restores the item', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(page.getByText('Deleted The Matrix')).toBeVisible();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Undo' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
  });

  test('deleting from the table view also shows Undo, and Undo restores the item', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(page.getByText('Deleted The Matrix')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Undo' }).click();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
  });
});
