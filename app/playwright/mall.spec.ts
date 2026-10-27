// The Mall and Castle library designs (#357): the switcher, rooms, presets,
// tap-to-identify, and the frame pacing probe.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

async function chooseDesign(page: Page, open: (path: string) => Promise<void>, design: 'Castle' | 'Mall') {
  await open('/settings');
  await page.getByRole('button', { name: `Library design ${design}` }).click();
  await open('/library');
  await page.getByRole('button', { name: 'Grid view' }).click();
}

async function firstItemIds(page: Page, count: number): Promise<(string | null)[]> {
  return page.getByTestId(/item-/).evaluateAll((nodes, n) => nodes.slice(0, n).map((node) => node.getAttribute('data-testid')), count);
}

test.describe('mall', () => {
  test('switches to Mall, browses rooms, and identifies a tapped cover', async ({ page, open, shot, expectAccessible, errors }) => {
    await chooseDesign(page, open, 'Mall');
    await expect(page.getByTestId('mall-room')).toBeVisible();
    const canvas = page.locator('canvas');
    await expect(canvas).toBeVisible();

    // The aisle list carries the seed library.
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' }).first()).toBeVisible();

    // Rooms narrow to their kinds.
    await page.getByRole('button', { name: 'Room Games' }).click();
    await expect(page.getByRole('button', { name: 'Super Mario 64, Cartridge, Nintendo 64' }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toHaveCount(0);

    // The camera presets glide without leaving the room.
    await page.getByRole('button', { name: 'Camera Wall' }).click();
    await expect(page.getByTestId('mall-room')).toBeVisible();

    await page.getByRole('button', { name: 'Room Mall' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' }).first()).toBeVisible();
    await page.waitForTimeout(500);
    await shot('library-mall');

    // Tapping a cover identifies it; the card opens the item.
    await expect(canvas).toBeVisible();
    const box = await canvas.boundingBox();
    if (!box) throw new Error('the mall canvas has no box');
    const center = { x: box.width / 2, y: box.height / 2 };
    for (const [dx, dy] of [
      [0, 0],
      [50, 0],
      [-50, 0],
      [0, 40],
      [0, -40],
    ]) {
      await canvas.click({ position: { x: center.x + dx, y: center.y + dy } });
      if (await page.getByTestId('mall-identify').isVisible()) break;
    }
    await expect(page.getByTestId('mall-identify')).toBeVisible();
    await page.getByRole('button', { name: 'Open item' }).click();
    await expect(page).toHaveURL(/\/item\/[0-9a-f-]+$/);

    await expectAccessible();
    expect(errors).toEqual([]);
  });

  test('mall room animates at interactive pacing', async ({ page, open }) => {
    await chooseDesign(page, open, 'Mall');
    await expect(page.locator('canvas')).toBeVisible();
    const stats = await page.evaluate(
      () =>
        new Promise<{ frames: number; median: number; p95: number }>((resolve) => {
          const deltas: number[] = [];
          let last = performance.now();
          const end = last + 3000;
          const sample = (now: number) => {
            deltas.push(now - last);
            last = now;
            if (now < end) requestAnimationFrame(sample);
            else {
              deltas.sort((a, b) => a - b);
              resolve({ frames: deltas.length, median: deltas[Math.floor(deltas.length / 2)], p95: deltas[Math.floor(deltas.length * 0.95)] });
            }
          };
          requestAnimationFrame(sample);
        }),
    );
    console.log(`mall pacing probe: ${JSON.stringify(stats)}`);
    expect(stats.frames).toBeGreaterThan(60);
  });

  test('castle shelves keep the grid order', async ({ page, open, shot, expectAccessible, errors }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    const gridOrder = await firstItemIds(page, 3);

    await chooseDesign(page, open, 'Castle');
    await expect(page.getByTestId('castle-shelf')).toBeVisible();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    // The seed library fills past twelve, so the shelf drops its markers (density.ts).
    await expect(page.getByText('Shelf 1')).toHaveCount(0);
    expect(await firstItemIds(page, 3)).toEqual(gridOrder);

    await expectAccessible();
    expect(errors).toEqual([]);
    await shot('library-castle');
  });
});
