import { expect, test } from './fixtures';

test.describe('table at desktop width', () => {
  test('the column chooser hides and shows columns', async ({ page, open, shot }) => {
    await open('/library');
    const grid = page.getByRole('grid', { name: 'Library table' });
    await expect(grid.getByRole('columnheader', { name: 'Format' })).toBeVisible();

    await page.getByRole('button', { name: 'Choose columns' }).click();
    await expect(page.getByRole('heading', { name: 'Columns' })).toBeVisible();
    await shot('column-chooser');
    await page.getByRole('button', { name: 'Hide Format column' }).click();
    await expect(grid.getByRole('columnheader', { name: 'Format' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Show Added column' }).click();
    await expect(grid.getByRole('columnheader', { name: 'Added' })).toBeVisible();
    await page.getByRole('button', { name: 'Close Columns' }).click();
    await expect(page.getByRole('heading', { name: 'Columns' })).toHaveCount(0);
  });

  test('selecting rows opens the bar, and Move to sets the location', async ({ page, open, shot }) => {
    await open('/library');
    await page.getByRole('checkbox', { name: 'Select Dune' }).click();
    await page.getByRole('checkbox', { name: 'Select Super Mario 64' }).click();
    const bar = page.getByRole('toolbar', { name: 'Selected items' });
    await expect(bar.getByText('2 selected')).toBeVisible();
    await shot('selection-bar');

    await bar.getByRole('button', { name: 'Move to' }).click();
    await expect(page.getByText('Move 2 items to')).toBeVisible();
    await page.getByTestId('move-to-a0000000-0000-4000-8000-000000000004').click();

    await expect(page.getByRole('button', { name: 'Change location of Dune, now Living room' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Change location of Super Mario 64, now Living room' })).toBeVisible();
  });

  test('select all then clear selection', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('checkbox', { name: 'Select all loaded items' }).click();
    const bar = page.getByRole('toolbar', { name: 'Selected items' });
    await expect(bar).toBeVisible();
    await bar.getByRole('button', { name: 'Clear selection' }).click();
    await expect(bar).toHaveCount(0);
  });

  test('an inline edit changes the status in place', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Change status of Dune, now Owned' }).click();
    // Missing is still in the library, so the row stays where it is.
    await page.getByRole('menuitem', { name: 'Set status of Dune to Missing' }).click();
    await expect(page.getByRole('button', { name: 'Change status of Dune, now Missing' })).toBeVisible();
    await expect(page).toHaveURL(/\/library$/);
  });

  // The wishlist and Sold are not the library, so a row changed to either leaves this list: the table says where it went, with Undo.
  test('moving a row to the wishlist says so, and Undo brings it back', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Change status of Dune, now Owned' }).click();
    await page.getByRole('menuitem', { name: 'Set status of Dune to Wishlist' }).click();

    await expect(page.getByText('Moved to Wishlist')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Change status of Dune, now Owned' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Undo' }).click();
    await expect(page.getByRole('button', { name: 'Change status of Dune, now Owned' })).toBeVisible();
    await expect(page.getByText('Moved to Wishlist')).toHaveCount(0);
  });
});
