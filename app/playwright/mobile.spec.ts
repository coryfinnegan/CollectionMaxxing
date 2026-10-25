import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

// The phone (iPhone-sized Chromium, light and dark): every page fits the width, and the chrome matches the content.
// Screenshots (SCREENSHOTS=<dir>) are for looking at, never committed.
const WIDTH = 390;
const MATRIX = 'c0000000-0000-4000-8000-000000000001';

const routes: { name: string; path: string }[] = [
  { name: 'library', path: '/library' },
  { name: 'wishlist', path: '/wishlist' },
  { name: 'lent-out', path: '/lent-out' },
  { name: 'item', path: `/item/${MATRIX}` },
  { name: 'item-edit', path: `/item/${MATRIX}/edit` },
  { name: 'review', path: '/review' },
  { name: 'me', path: '/me' },
  { name: 'devices', path: '/me/devices' },
  { name: 'sign-in-methods', path: '/me/sign-in-methods' },
  { name: 'locations', path: '/me/locations' },
  { name: 'export', path: '/me/export' },
  { name: 'deleted', path: '/me/deleted' },
  { name: 'delete-account', path: '/me/delete' },
  { name: 'members', path: '/members' },
  { name: 'settings', path: '/settings' },
  { name: 'about', path: '/about' },
  { name: 'evals', path: '/evals' },
  { name: 'scanner', path: '/scanner' },
  { name: 'result', path: '/result/c1000000-0000-4000-8000-000000000001' },
  { name: 'login', path: '/login' },
  { name: 'welcome', path: '/welcome?account=new' },
  { name: 'create-library', path: '/welcome/create' },
  { name: 'join-library', path: '/welcome/join' },
  { name: 'join', path: '/join/valid-invite' },
];

/** How far the page content reaches past the phone's width (0 when it fits). */
const overflow = (page: Page) => page.evaluate((w) => Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) - w, WIDTH);

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`phone, ${scheme} mode`, () => {
    test.use({ colorScheme: scheme });

    for (const { name, path } of routes) {
      test(`${name} fits the phone width`, async ({ page, open, shot }) => {
        await open(path);
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(400);
        await shot(`${scheme}-${name}`);
        expect(await overflow(page)).toBeLessThanOrEqual(0);
      });
    }

    test('the library table and grid fit, and the view toggle has room below it', async ({ page, open, shot }) => {
      await open('/library');
      const toolbar = page.getByRole('toolbar', { name: 'Library view' });
      await expect(toolbar).toBeVisible();
      // Below 768 px with no saved choice the library opens as the grid.
      await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
      await shot(`${scheme}-library-grid`);
      expect(await overflow(page)).toBeLessThanOrEqual(0);

      const gap = await page.evaluate(() => {
        const tb = document.querySelector('[role="toolbar"][aria-label="Library view"]') as HTMLElement;
        const next = tb.nextElementSibling as HTMLElement;
        const last = tb.lastElementChild as HTMLElement;
        return next.getBoundingClientRect().top - last.getBoundingClientRect().bottom;
      });
      expect(gap).toBeGreaterThanOrEqual(8);

      await page.getByRole('button', { name: 'Table view' }).click();
      await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
      await shot(`${scheme}-library-table`);
      expect(await overflow(page)).toBeLessThanOrEqual(0);
    });

    test('an empty table shows its message inside the screen', async ({ page, open, shot }) => {
      await open('/library');
      await page.getByRole('button', { name: 'Table view' }).click();
      await page.getByRole('textbox', { name: 'Search' }).fill('zzzz no such title');
      const message = page.getByRole('heading', { name: 'Nothing matches.' });
      await expect(message).toBeVisible();
      const hint = page.getByText('Try a different search or filter.');
      for (const target of [message, hint, page.getByRole('button', { name: 'Clear filters' })]) {
        const box = (await target.boundingBox())!;
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(WIDTH);
      }
      await shot(`${scheme}-library-empty`);
      expect(await overflow(page)).toBeLessThanOrEqual(0);
    });

    test('the tab bar, the stack header and the page use the theme colours', async ({ page, open, shot }) => {
      const dark = scheme === 'dark';
      const surface = dark ? 'rgb(25, 25, 24)' : 'rgb(253, 253, 252)';
      const ground = dark ? 'rgb(17, 17, 16)' : 'rgb(249, 249, 248)';
      /** The first painted background at or above whatever sits at this point of the screen. */
      const paintedAt = (x: number, y: number) =>
        page.evaluate(
          ([px, py]) => {
            for (let el: Element | null = document.elementFromPoint(px, py); el; el = el.parentElement) {
              const c = getComputedStyle(el).backgroundColor;
              if (c !== 'rgba(0, 0, 0, 0)') return c;
            }
            return null;
          },
          [x, y],
        );

      await open('/library');
      await expect(page.getByRole('tab', { name: 'Library' })).toBeVisible();
      expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe(ground);
      expect(await page.evaluate(() => {
        for (let el: Element | null = document.querySelector('[role="tablist"]'); el; el = el.parentElement) {
          const c = getComputedStyle(el).backgroundColor;
          if (c !== 'rgba(0, 0, 0, 0)') return c;
        }
        return null;
      })).toBe(surface);

      // A page above the tabs keeps its title bar; it must be themed as well.
      await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
      await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
      await shot(`${scheme}-item-header`);
      expect(await paintedAt(WIDTH / 2, 20)).toBe(surface);
    });

    test('everything a finger presses is at least 44 px', async ({ page, open }) => {
      const tooSmall = () =>
        page.evaluate(() => {
          const found: string[] = [];
          const pressable = 'button, a, input, textarea, select, [role=button], [role=link], [role=tab], [role=checkbox], [role=switch], [role=radio]';
          for (const el of Array.from(document.querySelectorAll(pressable))) {
            if (el.getAttribute('aria-label') === 'Skip to main content') continue; // off screen until it has keyboard focus
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            if (r.height < 43.5 || r.width < 43.5) found.push(`${el.getAttribute('aria-label') ?? el.textContent?.slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
          }
          return found;
        });
      await open('/library');
      await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
      expect(await tooSmall()).toEqual([]);
      await page.getByRole('button', { name: 'Table view' }).click();
      await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
      expect(await tooSmall()).toEqual([]);
      await page.goto(`/item/${MATRIX}/edit`);
      await expect(page.getByRole('button', { name: 'Save changes' })).toBeVisible();
      expect(await tooSmall()).toEqual([]);
      await page.goto('/about');
      await expect(page.getByRole('heading', { name: 'About' }).first()).toBeVisible();
      expect(await tooSmall()).toEqual([]);
    });

    test('Library is named once on the phone', async ({ page, open }) => {
      await open('/library');
      await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Library', exact: true })).toHaveCount(1);
    });
  });
}

test.describe('phone chrome in dark mode', () => {
  test.use({ colorScheme: 'dark' });

  test('no light band sits across the dark content', async ({ page, open, shot }) => {
    await open('/library');
    await expect(page.getByRole('tab', { name: 'Library' })).toBeVisible();
    await page.waitForTimeout(300);
    await shot('dark-chrome');
    const lights = await page.evaluate(() => {
      const found: string[] = [];
      const luminance = (c: string) => {
        const m = c.match(/[\d.]+/g);
        if (!m) return 0;
        const [r, g, b, a = '1'] = m.map(Number);
        return Number(a) === 0 ? 0 : (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
      };
      for (const el of Array.from(document.querySelectorAll('body *'))) {
        const r = el.getBoundingClientRect();
        if (r.width < 200 || r.height < 20 || r.bottom <= 0 || r.top >= window.innerHeight) continue;
        const bgc = getComputedStyle(el).backgroundColor;
        if (luminance(bgc) > 0.6) found.push(`${el.tagName} y=${Math.round(r.top)} h=${Math.round(r.height)} ${bgc}`);
      }
      return found;
    });
    expect(lights).toEqual([]);
  });
});
