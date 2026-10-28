import { expect, test } from './fixtures';

// The web app works with the keyboard alone: a skip link, arrow keys in the table, Escape closes panels and puts focus back.
test.describe('keyboard only', () => {
  test('the skip link is the first stop, shows itself, and moves focus to the page', async ({ page, open }) => {
    await open('/library');
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Skip to main content' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();
    // Tab then leaves the main region from its first control, not the sidebar.
    await page.keyboard.press('Tab');
    const inMain = await page.evaluate(() => !!document.activeElement?.closest('#main-content'));
    expect(inMain).toBe(true);
  });

  test('the library table moves between rows with the arrow keys and keeps a sensible tab order', async ({ page, open }) => {
    await open('/library');
    const all = page.getByRole('checkbox', { name: 'Select all loaded items' });
    const rows = page.getByRole('row').filter({ has: page.getByRole('checkbox', { name: /^Select (?!all)/ }) });
    // The header (and its Select-all checkbox) renders before the items query resolves, so an ArrowDown
    // pressed too early has no neighbor row to move to and focus never lands (#482). Wait for the rows first.
    await expect(rows.nth(1)).toBeVisible();
    await all.focus();
    await expect(all).toBeFocused();
    await page.keyboard.press('ArrowDown');
    const first = rows.nth(0).getByRole('checkbox');
    const second = rows.nth(1).getByRole('checkbox');
    await expect(first).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(second).toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(first).toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(all).toBeFocused();

    // Arrow Right goes along the row, to the title link; Tab does the same.
    await first.focus();
    await page.keyboard.press('ArrowRight');
    await expect(rows.nth(0).getByRole('link').first()).toBeFocused();
    await page.keyboard.press('ArrowLeft');
    await expect(first).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(rows.nth(0).getByRole('link').first()).toBeFocused();

    // Space toggles the row's checkbox.
    await first.focus();
    await page.keyboard.press('Space');
    await expect(first).toHaveAttribute('aria-checked', 'true');
  });

  test('Escape closes the column chooser and puts focus back on the button that opened it', async ({ page, open }) => {
    await open('/library');
    const opener = page.getByRole('button', { name: 'Choose columns' });
    await opener.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Columns' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('heading', { name: 'Columns' })).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('Escape closes the invite panel and puts focus back on Invite someone', async ({ page, open }) => {
    await open('/members');
    const opener = page.getByRole('button', { name: 'Invite someone' });
    await opener.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('textbox', { name: 'Name' })).toBeVisible();
    // Focus moved into the panel, so typing goes there.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('Escape closes the Add an item panel and returns focus', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click(); // the table has no Add item button yet; the grid does
    const opener = page.getByRole('button', { name: 'Add item' });
    await opener.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Add an item' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('heading', { name: 'Add an item' })).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('no keyboard trap: Tab keeps moving through the whole library page', async ({ page, open }) => {
    await open('/library');
    const seen = new Set<string>();
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Tab');
      seen.add(await page.evaluate(() => (document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent ?? '').slice(0, 40)));
    }
    expect(seen.size).toBeGreaterThan(10);
  });

  test('focus is visible on the controls', async ({ page, open }) => {
    await open('/library');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    const ring = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement;
      const s = getComputedStyle(el);
      return { outlineStyle: s.outlineStyle, outlineWidth: parseFloat(s.outlineWidth), boxShadow: s.boxShadow };
    });
    expect(ring.outlineStyle !== 'none' && ring.outlineWidth >= 2 ? true : ring.boxShadow !== 'none').toBe(true);
  });
});
