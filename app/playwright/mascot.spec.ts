// The mascot contact sheet (#365): all ten poses, screenshotted against a
// committed snapshot. Regenerate deliberately with
// `npx playwright test mascot --update-snapshots` and review the diff.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

const cells = [
  'mascot-cell-idle',
  'mascot-cell-reading',
  'mascot-cell-sleeping',
  'mascot-cell-celebrating',
  'mascot-cell-searching',
  'mascot-cell-emptyHoard',
  'mascot-cell-oops',
  'mascot-cell-milestone',
  'mascot-cell-fastReading',
  'mascot-cell-refreshPeek',
];

async function expectSheet(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await page.addStyleTag({ content: '* { scrollbar-width: none !important; } *::-webkit-scrollbar { display: none !important; }' });
  await expect(page).toHaveScreenshot(`${name}.png`, { animations: 'disabled', fullPage: true, maxDiffPixelRatio: 0.015 });
}

test.describe('mascot contact sheet', () => {
  test('all ten poses share one set of layers', async ({ page, open, expectAccessible, shot }) => {
    await open('/mascot-sheet');
    for (const cell of cells) await expect(page.getByTestId(cell)).toBeVisible();
    await expectAccessible();
    await expectSheet(page, 'mascot-sheet');
    await shot('mascot-sheet');
  });
});
