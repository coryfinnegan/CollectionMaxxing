// The castle contact sheet (#361): all five primitives in Full, Flat and
// Accessible, screenshotted against a committed snapshot. Regenerate
// deliberately with `npx playwright test castle --update-snapshots`.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

const cells = [
  'castle-cell-header-full',
  'castle-cell-header-flat',
  'castle-cell-header-accessible',
  'castle-cell-tablet-full',
  'castle-cell-tablet-flat',
  'castle-cell-tablet-accessible',
  'castle-cell-banner-full',
  'castle-cell-banner-flat',
  'castle-cell-banner-accessible',
  'castle-cell-banner-media-book',
  'castle-cell-banner-media-game',
  'castle-cell-banner-media-film',
  'castle-cell-header-perch',
  'castle-cell-shield',
  'castle-cell-hoard',
  'castle-cell-state',
  'castle-cell-ledge-full',
  'castle-cell-ledge-flat',
  'castle-cell-ledge-accessible',
  'castle-cell-parchment-full',
  'castle-cell-parchment-flat',
  'castle-cell-parchment-accessible',
];

async function expectSheet(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  // Fonts ready is not images ready: the texture tiles load after, and a shot
  // taken too early freezes half-loaded fills. Broken images resolve too (the
  // snapshot then shows the damage instead of hanging the spec).
  await page.evaluate(async () => {
    const imgs = Array.from(document.querySelectorAll('img'));
    await Promise.all(
      imgs.map(
        (img) =>
          (img.complete && img.naturalWidth > 0) ||
          new Promise<void>((done) => {
            img.addEventListener('load', () => done(), { once: true });
            img.addEventListener('error', () => done(), { once: true });
          }),
      ),
    );
  });
  await page.addStyleTag({ content: '* { scrollbar-width: none !important; } *::-webkit-scrollbar { display: none !important; }' });
  await expect(page).toHaveScreenshot(`${name}.png`, { animations: 'disabled', fullPage: true, maxDiffPixelRatio: 0.015 });
}

test.describe('castle contact sheet', () => {
  test('all primitives in all modes', async ({ page, open, expectAccessible, shot }) => {
    await open('/castle-sheet');
    for (const cell of cells) await expect(page.getByTestId(cell)).toBeVisible();
    await expectAccessible();
    await expectSheet(page, 'castle-sheet');
    await shot('castle-sheet');
  });
});
