// The motion pass on the web (#304): empty states draw their spot art, the grid
// tile still opens its item (the shared flight is native-only; the web pushes),
// and Reduce Motion leaves the words alone.
import { expect, test } from './fixtures';

test.describe('motion', () => {
  test('no search results show the spot art', async ({ page, open, shot }) => {
    await open('/library');
    await page.getByRole('textbox', { name: 'Search' }).fill('zzzz no such title');
    await expect(page.getByRole('heading', { name: 'Nothing matches.' })).toBeVisible();
    await expect(page.getByTestId('empty-spot')).toHaveCount(1);
    await shot('motion-no-results');
  });

  test('an empty review shows the spot art', async ({ page, open, shot }) => {
    await open('/review');
    await expect(page.getByRole('heading', { name: 'Nothing to review.' })).toBeVisible();
    await expect(page.getByTestId('empty-spot')).toHaveCount(1);
    await shot('motion-review-empty');
  });

  test('a grid tile still opens its item through the animated wrapper', async ({ page, open, shot }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click();
    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await shot('motion-item-detail');
  });

  test.describe('with Reduce Motion on', () => {
    test.use({ reducedMotion: 'reduce' });

    test('the words stay but the spot art goes away', async ({ page, open, shot }) => {
      await open('/review');
      await expect(page.getByRole('heading', { name: 'Nothing to review.' })).toBeVisible();
      await expect(page.getByTestId('empty-spot')).toHaveCount(0);
      await shot('motion-reduced');
    });
  });
});
